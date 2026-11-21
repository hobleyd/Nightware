using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

using Nightware.Integration.Tests.Tasks;

namespace Nightware.Integration.Tests.MailboxSettings;

/// <summary>mailboxSettings against the live stack: time zone in Keycloak, automatic replies in Stalwart.</summary>
public sealed class MailboxSettingsLiveTests : IAsyncLifetime
{
    private GroupwareLiveHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _host = GroupwareLiveHost.Start();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.DisposeAsync();
        }
    }

    [Fact]
    public async Task The_time_zone_round_trips_as_a_windows_id()
    {
        using var graph = await _host.GraphAsync(GroupwareLiveHost.Bob, Ct);
        var original = (await GetAsync(graph))["timeZone"]!.GetValue<string>();
        try
        {
            var changed = await graph.PatchAsJsonAsync("/v1.0/me/mailboxSettings", new { timeZone = "Tokyo Standard Time" }, Ct);
            changed.StatusCode.ShouldBe(HttpStatusCode.OK, await changed.Content.ReadAsStringAsync(Ct));
            (await GetAsync(graph))["timeZone"]!.GetValue<string>().ShouldBe("Tokyo Standard Time");

            var iana = await graph.PatchAsJsonAsync("/v1.0/me/mailboxSettings", new { timeZone = "Europe/Paris" }, Ct);
            iana.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await GetAsync(graph))["timeZone"]!.GetValue<string>().ShouldBe("Romance Standard Time");
        }
        finally
        {
            await graph.PatchAsJsonAsync("/v1.0/me/mailboxSettings", new { timeZone = original }, Ct);
        }
    }

    [Fact]
    public async Task Another_users_time_zone_cannot_be_changed()
    {
        using var graph = await _host.GraphAsync(GroupwareLiveHost.Bob, Ct);

        var response = await graph.PatchAsJsonAsync($"/v1.0/users/{GroupwareLiveHost.Address(GroupwareLiveHost.Alice)}/mailboxSettings", new { timeZone = "UTC" }, Ct);

        ((int)response.StatusCode).ShouldBeOneOf(403, 404);
    }

    [Fact]
    public async Task An_automatic_reply_answers_mail_from_another_user()
    {
        using var alice = await _host.GraphAsync(GroupwareLiveHost.Alice, Ct);
        var marker = "OOF-" + Guid.NewGuid().ToString("N")[..10];
        var now = DateTime.UtcNow;
        try
        {
            var set = await alice.PatchAsJsonAsync("/v1.0/me/mailboxSettings", new
            {
                automaticRepliesSetting = new
                {
                    status = "scheduled",
                    externalAudience = "all",
                    internalReplyMessage = $"<p>Away. {marker}</p>",
                    externalReplyMessage = $"<p>Away. {marker}</p>",
                    scheduledStartDateTime = new { dateTime = Wall(now.AddHours(-1)), timeZone = "UTC" },
                    scheduledEndDateTime = new { dateTime = Wall(now.AddDays(1)), timeZone = "UTC" },
                },
            }, Ct);
            set.StatusCode.ShouldBe(HttpStatusCode.OK, await set.Content.ReadAsStringAsync(Ct));

            var read = await GetAsync(alice);
            var replies = read["automaticRepliesSetting"]!;
            replies["status"]!.GetValue<string>().ShouldBe("scheduled");
            replies["externalReplyMessage"]!.GetValue<string>().ShouldContain(marker);
            var zone = read["timeZone"]!.GetValue<string>();
            replies["scheduledStartDateTime"]!["timeZone"]!.GetValue<string>().ShouldBe(zone);

            await _host.SendAsync(GroupwareLiveHost.Bob, GroupwareLiveHost.Address(GroupwareLiveHost.Alice), "Live OOF probe " + marker, "Are you there?", Ct);

            var reply = await _host.WaitForMessageAsync(GroupwareLiveHost.Bob, marker, TimeSpan.FromSeconds(90), Ct);
            reply.ShouldNotBeNull($"no automatic reply containing {marker} reached bob");
            reply["from"]![0]!["email"]!.GetValue<string>().ShouldBe(GroupwareLiveHost.Address(GroupwareLiveHost.Alice), StringCompareShould.IgnoreCase);
        }
        finally
        {
            await alice.PatchAsJsonAsync("/v1.0/me/mailboxSettings", new { automaticRepliesSetting = new { status = "disabled", externalAudience = "all", internalReplyMessage = string.Empty, externalReplyMessage = string.Empty } }, Ct);
        }

        (await GetAsync(alice))["automaticRepliesSetting"]!["status"]!.GetValue<string>().ShouldBe("disabled");
    }

    [Fact]
    public async Task Internal_senders_get_the_internal_reply_when_the_two_differ()
    {
        using var alice = await _host.GraphAsync(GroupwareLiveHost.Alice, Ct);
        var inside = "OOF-IN-" + Guid.NewGuid().ToString("N")[..10];
        var outside = "OOF-OUT-" + Guid.NewGuid().ToString("N")[..10];
        try
        {
            var set = await alice.PatchAsJsonAsync("/v1.0/me/mailboxSettings", new
            {
                automaticRepliesSetting = new
                {
                    status = "alwaysEnabled",
                    externalAudience = "none",
                    internalReplyMessage = $"<p>Back soon, colleague. {inside}</p>",
                    externalReplyMessage = $"<p>Out of the office. {outside}</p>",
                },
            }, Ct);
            set.StatusCode.ShouldBe(HttpStatusCode.OK, await set.Content.ReadAsStringAsync(Ct));

            var replies = (await GetAsync(alice))["automaticRepliesSetting"]!;
            replies["externalAudience"]!.GetValue<string>().ShouldBe("none");
            replies["internalReplyMessage"]!.GetValue<string>().ShouldContain(inside);
            replies["externalReplyMessage"]!.GetValue<string>().ShouldContain(outside);

            // Bob shares Alice's domain, so he is inside the organisation.
            await _host.SendAsync(GroupwareLiveHost.Bob, GroupwareLiveHost.Address(GroupwareLiveHost.Alice), "Live split OOF probe " + inside, "Are you there?", Ct);
            var reply = await _host.WaitForMessageAsync(GroupwareLiveHost.Bob, inside, TimeSpan.FromSeconds(90), Ct);
            reply.ShouldNotBeNull($"no internal automatic reply containing {inside} reached bob");
        }
        finally
        {
            await alice.PatchAsJsonAsync("/v1.0/me/mailboxSettings", new { automaticRepliesSetting = new { status = "disabled", externalAudience = "all", internalReplyMessage = string.Empty, externalReplyMessage = string.Empty } }, Ct);
        }
    }

    private static string Wall(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss'.0000000'", CultureInfo.InvariantCulture);

    private static async Task<JsonObject> GetAsync(HttpClient graph)
    {
        var response = await graph.GetAsync("/v1.0/me/mailboxSettings?$select=automaticRepliesSetting,timeZone", Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
    }
}

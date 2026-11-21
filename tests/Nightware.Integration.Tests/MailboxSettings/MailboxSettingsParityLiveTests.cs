using System.Net;
using System.Text.Json.Nodes;

using Nightware.Integration.Tests.Directory;
using Nightware.Integration.Tests.Support;

namespace Nightware.Integration.Tests.MailboxSettings;

/// <summary>Phase 18 against the dev stack: single settings read from the directory, and the supported lists.</summary>
public sealed class MailboxSettingsParityLiveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonObject> GetAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, text);
        return JsonNode.Parse(text)!.AsObject();
    }

    [Fact]
    public async Task Single_settings_agree_with_the_whole_and_the_lists_offer_the_mailboxs_zone()
    {
        Assert.SkipUnless(DevStack.Enabled, "Set NIGHTWARE_INTEGRATION=1 with the dev stack running.");
        await using var stack = LiveStack.Start();
        using var bob = await stack.GraphClientAsync("bob", Ct);

        var whole = await GetAsync(bob, "/v1.0/me/mailboxSettings");
        var zone = (await GetAsync(bob, "/v1.0/me/mailboxSettings/timeZone"))["value"]!.GetValue<string>();
        zone.ShouldBe(whole["timeZone"]!.GetValue<string>());
        (await GetAsync(bob, "/v1.0/me/mailboxSettings/automaticRepliesSetting"))["status"]!.GetValue<string>()
            .ShouldBe(whole["automaticRepliesSetting"]!["status"]!.GetValue<string>());

        var hours = await GetAsync(bob, "/v1.0/me/mailboxSettings/workingHours");
        hours["daysOfWeek"]!.AsArray().Count.ShouldBeGreaterThan(0);
        (await GetAsync(bob, "/v1.0/me/mailboxSettings/language"))["locale"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
        (await GetAsync(bob, "/v1.0/me/mailboxSettings/dateFormat"))["value"]!.GetValue<string>().ShouldNotBeNullOrEmpty();

        var zones = (await GetAsync(bob, "/v1.0/me/outlook/supportedTimeZones"))["value"]!.AsArray();
        zones.Select(z => z!["alias"]!.GetValue<string>()).ShouldContain(zone, "the mailbox's own zone is one it may choose");
        (await GetAsync(bob, "/v1.0/me/outlook/supportedTimeZones(TimeZoneStandard=microsoft.graph.timeZoneStandard'Iana')"))["value"]!.AsArray()
            .ShouldContain(z => z!["alias"]!.GetValue<string>() == "Australia/Sydney");
        (await GetAsync(bob, "/v1.0/me/outlook/supportedLanguages"))["value"]!.AsArray()
            .ShouldContain(l => l!["locale"]!.GetValue<string>() == "en-US");
    }
}

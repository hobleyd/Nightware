using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

using Nightware.Integration.Tests.Directory;
using Nightware.Integration.Tests.Support;

namespace Nightware.Integration.Tests.Parity;

/// <summary>Phase 19 aliases against the dev stack: an item's actions and parts under its parent, and the default calendar's own routes.</summary>
public sealed class ItemAliasLiveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonObject> SendAsync(HttpClient client, HttpMethod method, string path, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        using var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, text);
        return text.Length == 0 || text[0] != '{' ? [] : JsonNode.Parse(text)!.AsObject();
    }

    private static string Id(JsonNode node) => Uri.EscapeDataString(node["id"]!.GetValue<string>());

    [Fact]
    public async Task Parent_scoped_routes_reach_the_same_items()
    {
        Assert.SkipUnless(DevStack.Enabled, "Set NIGHTWARE_INTEGRATION=1 with the dev stack running.");
        await using var stack = LiveStack.Start();
        using var bob = await stack.GraphClientAsync("bob", Ct);
        var tag = "nw-alias19-" + Guid.NewGuid().ToString("N")[..8];

        // The default calendar: renamed through calendar (and put back), its permissions and its group.
        var calendar = await SendAsync(bob, HttpMethod.Get, "/v1.0/me/calendar");
        var name = calendar["name"]!.GetValue<string>();
        try
        {
            (await SendAsync(bob, HttpMethod.Patch, "/v1.0/me/calendar", new { name = tag }))["name"]!.GetValue<string>().ShouldBe(tag);
            (await SendAsync(bob, HttpMethod.Get, $"/v1.0/me/calendars/{Id(calendar)}"))["name"]!.GetValue<string>().ShouldBe(tag);
        }
        finally
        {
            await SendAsync(bob, HttpMethod.Patch, "/v1.0/me/calendar", new { name });
        }

        (await SendAsync(bob, HttpMethod.Get, "/v1.0/me/calendar/calendarPermissions"))["value"].ShouldNotBeNull();
        (await SendAsync(bob, HttpMethod.Get, "/v1.0/me/calendarGroups/calendargroup-default"))["name"]!.GetValue<string>().ShouldBe("My Calendars");

        // An event's attachments through calendars/{id}/events/{id} and calendar/events/{id}.
        var start = DateTime.UtcNow.Date.AddDays(5).AddHours(9);
        var created = await SendAsync(bob, HttpMethod.Post, "/v1.0/me/events", new
        {
            subject = tag,
            start = new { dateTime = start.ToString("s"), timeZone = "UTC" },
            end = new { dateTime = start.AddHours(1).ToString("s"), timeZone = "UTC" },
        }, HttpStatusCode.Created);
        try
        {
            await SendAsync(bob, HttpMethod.Post, $"/v1.0/me/calendars/{Id(calendar)}/events/{Id(created)}/attachments", new Dictionary<string, object>
            {
                ["@odata.type"] = "#microsoft.graph.fileAttachment",
                ["name"] = "notes.txt",
                ["contentBytes"] = Convert.ToBase64String("hello"u8.ToArray()),
            }, HttpStatusCode.Created);
            var attachment = (await SendAsync(bob, HttpMethod.Get, $"/v1.0/me/calendar/events/{Id(created)}/attachments"))["value"]!.AsArray().ShouldHaveSingleItem()!;
            (await bob.GetStringAsync($"/v1.0/me/calendar/events/{Id(created)}/attachments/{Id(attachment)}/$value", Ct)).ShouldBe("hello");
            await SendAsync(bob, HttpMethod.Post, $"/v1.0/me/calendar/events/{Id(created)}/dismissReminder", expected: HttpStatusCode.OK);
        }
        finally
        {
            await SendAsync(bob, HttpMethod.Delete, $"/v1.0/me/events/{Id(created)}", expected: HttpStatusCode.NoContent);
        }

        // A message's MIME through its folder.
        var inbox = await SendAsync(bob, HttpMethod.Get, "/v1.0/me/mailFolders/inbox/messages?$select=id&$top=1");
        if (inbox["value"]!.AsArray().FirstOrDefault() is { } message)
        {
            (await bob.GetStringAsync($"/v1.0/me/mailFolders/inbox/messages/{Id(message)}/$value", Ct)).ShouldContain("Message-ID", Case.Insensitive);
        }
    }
}

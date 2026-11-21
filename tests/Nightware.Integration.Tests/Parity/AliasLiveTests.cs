using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

using Nightware.Integration.Tests.Directory;
using Nightware.Integration.Tests.Support;

namespace Nightware.Integration.Tests.Parity;

/// <summary>Phase 18 aliases against the dev stack: Graph's other routes to the same items.</summary>
public sealed class AliasLiveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonObject> SendAsync(HttpClient client, HttpMethod method, string path, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        using var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, text);
        return text.Length == 0 ? [] : JsonNode.Parse(text)!.AsObject();
    }

    [Fact]
    public async Task Default_calendar_and_folder_scoped_routes_reach_the_same_items()
    {
        Assert.SkipUnless(DevStack.Enabled, "Set NIGHTWARE_INTEGRATION=1 with the dev stack running.");
        await using var stack = LiveStack.Start();
        using var bob = await stack.GraphClientAsync("bob", Ct);
        var tag = "nw-alias-" + Guid.NewGuid().ToString("N")[..8];
        var start = DateTime.UtcNow.Date.AddDays(3).AddHours(9);

        // An event created through calendar/events lands in the default calendar and is reachable under it.
        var calendarId = (await SendAsync(bob, HttpMethod.Get, "/v1.0/me/calendar?$select=id"))["id"]!.GetValue<string>();
        var created = await SendAsync(bob, HttpMethod.Post, "/v1.0/me/calendar/events", new
        {
            subject = tag,
            start = new { dateTime = start.ToString("s"), timeZone = "UTC" },
            end = new { dateTime = start.AddHours(1).ToString("s"), timeZone = "UTC" },
        }, HttpStatusCode.Created);
        var eventId = Uri.EscapeDataString(created["id"]!.GetValue<string>());
        try
        {
            (await SendAsync(bob, HttpMethod.Get, $"/v1.0/me/calendars/{Uri.EscapeDataString(calendarId)}/events/{eventId}?$select=subject"))["subject"]!.GetValue<string>().ShouldBe(tag);
            await SendAsync(bob, HttpMethod.Patch, $"/v1.0/me/calendar/events/{eventId}", new { subject = tag + " moved" });
            var view = await SendAsync(bob, HttpMethod.Get,
                $"/v1.0/me/calendar/calendarView?startDateTime={start.AddHours(-1):s}Z&endDateTime={start.AddHours(2):s}Z&$select=subject");
            view["value"]!.AsArray().ShouldContain(e => e!["subject"]!.GetValue<string>() == tag + " moved");
            (await SendAsync(bob, HttpMethod.Get, "/v1.0/me/calendar/events?$select=subject&$top=100"))["value"]!.AsArray()
                .ShouldContain(e => e!["subject"]!.GetValue<string>() == tag + " moved");
        }
        finally
        {
            await SendAsync(bob, HttpMethod.Delete, $"/v1.0/me/calendar/events/{eventId}", expected: HttpStatusCode.NoContent);
        }

        // A message under its folder, and a contact under its folder.
        var inbox = await SendAsync(bob, HttpMethod.Get, "/v1.0/me/mailFolders/inbox/messages?$select=id,subject&$top=1");
        if (inbox["value"]!.AsArray().FirstOrDefault() is { } message)
        {
            var id = Uri.EscapeDataString(message["id"]!.GetValue<string>());
            (await SendAsync(bob, HttpMethod.Get, $"/v1.0/me/mailFolders/inbox/messages/{id}?$select=id"))["id"]!.GetValue<string>()
                .ShouldBe(message["id"]!.GetValue<string>());
        }

        // The default folder isn't listed under contactFolders (as in Graph); the contact names its parent.
        var contact = await SendAsync(bob, HttpMethod.Post, "/v1.0/me/contacts", new { givenName = tag }, HttpStatusCode.Created);
        var folderId = contact["parentFolderId"]!.GetValue<string>();
        var contactPath = $"/v1.0/me/contactFolders/{Uri.EscapeDataString(folderId)}/contacts/{Uri.EscapeDataString(contact["id"]!.GetValue<string>())}";
        (await SendAsync(bob, HttpMethod.Get, contactPath + "?$select=givenName"))["givenName"]!.GetValue<string>().ShouldBe(tag);
        await SendAsync(bob, HttpMethod.Delete, contactPath, expected: HttpStatusCode.NoContent);
    }
}

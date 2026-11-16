using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Nightware.Integration.Tests.Support;

namespace Nightware.Integration.Tests.Calendar;

/// <summary>
/// Phase 4 against the running stack. Every test uses a unique subject and a slot far in the future so
/// parallel runs and other agents don't collide, and removes what it created.
/// </summary>
public sealed class CalendarLiveTests
{
    private static readonly string Alice = DevStack.Address("alice");
    private static readonly string Bob = DevStack.Address("bob");
    private static readonly string Boardroom = DevStack.Address("boardroom");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonObject> SendJsonAsync(HttpClient client, HttpMethod method, string path, string? body = null, HttpStatusCode expected = HttpStatusCode.OK, bool preferUtc = false)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        if (preferUtc)
        {
            request.Headers.Add("Prefer", "outlook.timezone=\"UTC\"");
        }

        using var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, $"{method} {path}: {text}");
        return text.Length == 0 ? [] : JsonNode.Parse(text)!.AsObject();
    }

    /// <summary>Polls Bob's inbox through the façade's mail routes until Nextcloud's iMIP invitation arrives.</summary>
    private static async Task<string> InvitationIdAsync(HttpClient client, string subject)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var page = await SendJsonAsync(client, HttpMethod.Get, "/v1.0/me/mailFolders/inbox/messages?$select=id,subject&$top=50");
            var match = page["value"]!.AsArray().FirstOrDefault(m => m!["subject"]?.GetValue<string>().Contains(subject, StringComparison.Ordinal) == true);
            if (match is not null)
            {
                return match["id"]!.GetValue<string>();
            }

            await Task.Delay(TimeSpan.FromSeconds(2), Ct);
        }

        throw new ShouldAssertException($"The invitation '{subject}' never reached the inbox.");
    }

    /// <summary>A random whole-hour slot in 2028 (Sydney), so rooms and people are free and runs don't overlap.</summary>
    private static DateTime UniqueSlot() =>
        new DateTime(2028, 1, 3, 9, 0, 0).AddDays(Random.Shared.Next(0, 300) * 7).AddHours(Random.Shared.Next(0, 8));

    private static string Wall(DateTime value) => value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    [Fact]
    public async Task A_meeting_invitation_travels_by_imip_and_the_rsvp_comes_back_to_the_organizer()
    {
        await using var stack = CalendarLiveStack.Start();
        using var alice = await stack.GraphClientAsync("alice", Ct);
        using var bob = await stack.GraphClientAsync("bob", Ct);
        var subject = $"P4 live invite {Guid.NewGuid():N}";
        var slot = UniqueSlot();

        var created = await SendJsonAsync(alice, HttpMethod.Post, "/v1.0/me/events", $$$"""
            {"subject":"{{{subject}}}","isAllDay":false,"isReminderOn":true,"reminderMinutesBeforeStart":15,
             "body":{"contentType":"text","content":"Agenda"},
             "start":{"dateTime":"{{{Wall(slot)}}}","timeZone":"Australia/Sydney"},
             "end":{"dateTime":"{{{Wall(slot.AddHours(1))}}}","timeZone":"Australia/Sydney"},
             "locations":[{"displayName":"Boardroom","locationEmailAddress":"{{{Boardroom}}}","locationType":"conferenceRoom"}],
             "location":{"displayName":"Boardroom","locationEmailAddress":"{{{Boardroom}}}","locationType":"conferenceRoom"},
             "attendees":[{"emailAddress":{"address":"{{{Bob}}}"},"type":"required"},{"emailAddress":{"address":"{{{Boardroom}}}","name":"Boardroom"},"type":"resource"}]}
            """, HttpStatusCode.Created);
        var eventId = created["id"]!.GetValue<string>();
        try
        {
            created["isOrganizer"]!.GetValue<bool>().ShouldBeTrue();
            created["start"]!["timeZone"]!.GetValue<string>().ShouldBe("Australia/Sydney");
            created["location"]!["locationEmailAddress"]!.GetValue<string>().ShouldBe(Boardroom);
            var room = created["attendees"]!.AsArray().Single(a => a!["emailAddress"]!["address"]!.GetValue<string>() == Boardroom)!;
            room["type"]!.GetValue<string>().ShouldBe("resource");
            room["status"]!["response"]!.GetValue<string>().ShouldBe("accepted", "Sabre auto-schedules a free room");

            // Bob receives the iMIP mail; the façade resolves it to his copy of the meeting (each cast spelling).
            var messageId = Uri.EscapeDataString(await InvitationIdAsync(bob, subject));
            var linked = await SendJsonAsync(bob, HttpMethod.Get, $"/v1.0/me/messages/{messageId}/microsoft.graph.eventMessageRequest/event?$select=id,isOrganizer");
            linked["isOrganizer"]!.GetValue<bool>().ShouldBeFalse();
            var uncast = await SendJsonAsync(bob, HttpMethod.Get, $"/v1.0/me/messages/{messageId}/event?$select=id");
            uncast["id"]!.GetValue<string>().ShouldBe(linked["id"]!.GetValue<string>());

            await SendJsonAsync(bob, HttpMethod.Post, $"/v1.0/me/messages/{messageId}/accept", """{"sendResponse":true,"comment":"See you there"}""", HttpStatusCode.Accepted);

            var bobsCopy = await SendJsonAsync(bob, HttpMethod.Get, $"/v1.0/me/events/{linked["id"]!.GetValue<string>()}?$select=responseStatus");
            bobsCopy["responseStatus"]!["response"]!.GetValue<string>().ShouldBe("accepted");

            // Sabre delivers Bob's iTIP REPLY to Alice's copy: PARTSTAT=ACCEPTED.
            var organizerCopy = await SendJsonAsync(alice, HttpMethod.Get, $"/v1.0/me/events/{eventId}?$select=attendees");
            organizerCopy["attendees"]!.AsArray().Single(a => a!["emailAddress"]!["address"]!.GetValue<string>() == Bob)!["status"]!["response"]!.GetValue<string>().ShouldBe("accepted");

            // Free/busy for Bob and the room now shows the meeting.
            var startUtc = TimeZoneInfo.ConvertTimeToUtc(slot, TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney"));
            var schedule = await SendJsonAsync(alice, HttpMethod.Post, "/v1.0/me/calendar/getSchedule", $$$"""
                {"schedules":["{{{Bob}}}","{{{Boardroom}}}"],
                 "startTime":{"dateTime":"{{{Wall(startUtc.AddHours(-1))}}}.000","timeZone":"UTC"},
                 "endTime":{"dateTime":"{{{Wall(startUtc.AddHours(2))}}}.000","timeZone":"UTC"}}
                """);
            var views = schedule["value"]!.AsArray().ToDictionary(s => s!["scheduleId"]!.GetValue<string>(), s => s!["availabilityView"]!.GetValue<string>());
            views[Bob].ShouldBe("002200");
            views[Boardroom].ShouldBe("002200");
        }
        finally
        {
            await SendJsonAsync(alice, HttpMethod.Post, $"/v1.0/me/events/{eventId}/cancel", """{"comment":""}""", HttpStatusCode.Accepted);
        }
    }

    [Fact]
    public async Task Calendar_view_expands_a_series_and_types_its_moved_occurrence_as_an_exception()
    {
        await using var stack = CalendarLiveStack.Start();
        using var alice = await stack.GraphClientAsync("alice", Ct);
        var subject = $"P4 live series {Guid.NewGuid():N}";
        var slot = UniqueSlot();
        var master = await SendJsonAsync(alice, HttpMethod.Post, "/v1.0/me/events", $$$"""
            {"subject":"{{{subject}}}","isAllDay":false,"isReminderOn":false,
             "start":{"dateTime":"{{{Wall(slot)}}}","timeZone":"Australia/Sydney"},
             "end":{"dateTime":"{{{Wall(slot.AddMinutes(30))}}}","timeZone":"Australia/Sydney"},
             "location":{"displayName":""},"locations":[],"attendees":[],
             "recurrence":{"pattern":{"type":"daily","interval":1},"range":{"type":"numbered","startDate":"{{{slot:yyyy-MM-dd}}}","numberOfOccurrences":3} } }
            """, HttpStatusCode.Created);
        var masterId = master["id"]!.GetValue<string>();
        try
        {
            var startUtc = TimeZoneInfo.ConvertTimeToUtc(slot, TimeZoneInfo.FindSystemTimeZoneById("Australia/Sydney"));
            var window = $"startDateTime={startUtc.AddHours(-1):yyyy-MM-ddTHH:mm:ss.fffZ}&endDateTime={startUtc.AddDays(3):yyyy-MM-ddTHH:mm:ss.fffZ}";
            async Task<List<JsonObject>> ViewAsync() =>
                (await SendJsonAsync(alice, HttpMethod.Get, $"/v1.0/me/calendarView?{window}&$top=100&$select=id,subject,start,seriesMasterId,recurrence", preferUtc: true))["value"]!
                .AsArray().Select(v => v!.AsObject()).Where(v => v["subject"]!.GetValue<string>().StartsWith(subject, StringComparison.Ordinal)).ToList();

            var occurrences = await ViewAsync();
            occurrences.Count.ShouldBe(3);
            occurrences.ShouldAllBe(o => o["seriesMasterId"]!.GetValue<string>() == masterId && o["recurrence"] == null);
            occurrences[0]["start"]!["timeZone"]!.GetValue<string>().ShouldBe("UTC");

            // Move the second occurrence by an hour: that occurrence only.
            var second = occurrences[1]["id"]!.GetValue<string>();
            await SendJsonAsync(alice, HttpMethod.Patch, $"/v1.0/me/events/{second}", $$$"""
                {"subject":"{{{subject}}} (moved)","start":{"dateTime":"{{{Wall(slot.AddDays(1).AddHours(1))}}}","timeZone":"Australia/Sydney"},"end":{"dateTime":"{{{Wall(slot.AddDays(1).AddHours(1).AddMinutes(30))}}}","timeZone":"Australia/Sydney"}}
                """);

            var after = await ViewAsync();
            after.Select(o => o["id"]!.GetValue<string>()).ShouldBe(occurrences.Select(o => o["id"]!.GetValue<string>()));
            after[1]["subject"]!.GetValue<string>().ShouldEndWith("(moved)");
            var moved = await SendJsonAsync(alice, HttpMethod.Get, $"/v1.0/me/events/{second}?$select=start", preferUtc: true);
            moved["start"]!["dateTime"]!.GetValue<string>().ShouldBe(startUtc.AddDays(1).AddHours(1).ToString("yyyy-MM-dd'T'HH:mm:ss'.0000000'", CultureInfo.InvariantCulture));

            var recurrence = await SendJsonAsync(alice, HttpMethod.Get, $"/v1.0/me/events/{masterId}?$select=recurrence");
            recurrence["recurrence"]!["pattern"]!["type"]!.GetValue<string>().ShouldBe("daily");
            recurrence["recurrence"]!["range"]!["numberOfOccurrences"]!.GetValue<int>().ShouldBe(3);

            // Deleting one occurrence leaves the other two.
            await SendJsonAsync(alice, HttpMethod.Delete, $"/v1.0/me/events/{occurrences[2]["id"]!.GetValue<string>()}", expected: HttpStatusCode.NoContent);
            (await ViewAsync()).Count.ShouldBe(2);
        }
        finally
        {
            await SendJsonAsync(alice, HttpMethod.Delete, $"/v1.0/me/events/{masterId}", expected: HttpStatusCode.NoContent);
        }
    }

    [Fact]
    public async Task Rooms_come_from_the_resource_directory()
    {
        await using var stack = CalendarLiveStack.Start();
        using var alice = await stack.GraphClientAsync("alice", Ct);

        var places = await SendJsonAsync(alice, HttpMethod.Get, "/v1.0/places/microsoft.graph.room?$top=500");
        var boardroom = places["value"]!.AsArray().Single(r => r!["emailAddress"]!.GetValue<string>() == Boardroom)!;
        boardroom["displayName"]!.GetValue<string>().ShouldBe("Boardroom");
        boardroom["capacity"]!.GetValue<int>().ShouldBe(12);
        boardroom["building"]!.GetValue<string>().ShouldBe("Nightware HQ");
        boardroom["floorNumber"]!.GetValue<int>().ShouldBe(1);
        boardroom["isWheelChairAccessible"]!.GetValue<bool>().ShouldBeTrue();

        var found = await SendJsonAsync(alice, HttpMethod.Get, "/beta/me/findRooms");
        found["value"]!.AsArray().Select(r => r!["address"]!.GetValue<string>()).ShouldContain(DevStack.Address("focus1"));
    }
}

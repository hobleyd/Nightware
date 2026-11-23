using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

using Nightware.Integration.Tests.Directory;
using Nightware.Integration.Tests.Support;

namespace Nightware.Integration.Tests.OnlineMeetings;

/// <summary>Phase 26 against the dev stack: alice's online meetings as Talk rooms, with recordings and transcripts (ADR 0009 D7).</summary>
public sealed class OnlineMeetingLiveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonObject> SendAsync(HttpClient client, HttpMethod method, string path, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        using var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, $"{method} {path}: {text}");
        return text.Length == 0 ? [] : JsonNode.Parse(text)!.AsObject();
    }

    private static string Filter(string url) => "$filter=" + Uri.EscapeDataString($"JoinWebUrl eq '{url}'");

    [Fact]
    public async Task Alice_creates_finds_renames_and_deletes_a_meeting_that_bob_sees_in_his_chats()
    {
        Assert.SkipUnless(DevStack.Enabled, "Set NIGHTWARE_INTEGRATION=1 with the dev stack running.");
        await using var live = TalkLive.Start();
        using var alice = await live.Stack.GraphClientAsync("alice", Ct);
        var bob = await SendAsync(alice, HttpMethod.Get, $"/v1.0/users/{DevStack.Address("bob")}");
        var bobId = bob["id"]!.GetValue<string>();

        var subject = "nw-meeting-" + Guid.NewGuid().ToString("N")[..8];
        var start = DateTimeOffset.UtcNow.AddDays(1);
        var meeting = await SendAsync(alice, HttpMethod.Post, "/v1.0/me/onlineMeetings", new
        {
            subject,
            startDateTime = start.ToString("O", CultureInfo.InvariantCulture),
            endDateTime = start.AddHours(1).ToString("O", CultureInfo.InvariantCulture),
            participants = new { attendees = new[] { new { identity = new { user = new { id = bobId } } } } },
        }, HttpStatusCode.Created);
        var id = meeting["id"]!.GetValue<string>();
        var token = meeting["chatInfo"]!["threadId"]!.GetValue<string>();
        try
        {
            id.ShouldBe("t." + token);
            meeting["subject"]!.GetValue<string>().ShouldBe(subject);
            meeting["joinWebUrl"]!.GetValue<string>().ShouldBe($"{DevStack.CloudBase}/call/{token}");
            meeting["startDateTime"]!.GetValue<DateTimeOffset>().ShouldBe(DateTimeOffset.FromUnixTimeSeconds(start.ToUnixTimeSeconds()));
            meeting["creationDateTime"]!.GetValue<DateTimeOffset>().ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddMinutes(-5));
            meeting["participants"]!["organizer"]!["identity"]!["user"]!["displayName"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
            meeting["participants"]!["attendees"]!.AsArray().Select(a => a!["identity"]!["user"]!["id"]!.GetValue<string>()).ShouldBe([bobId]);

            // bob has it among his conversations.
            var (_, rooms) = await live.OcsAsync("bob", HttpMethod.Get, "apps/spreed/api/v4/room", Ct);
            rooms.EnumerateArray().Select(r => r.GetProperty("token").GetString()).ShouldContain(token);

            (await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/onlineMeetings/{id}"))["id"]!.GetValue<string>().ShouldBe(id);
            var found = await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/onlineMeetings?{Filter(meeting["joinWebUrl"]!.GetValue<string>())}");
            found["value"]!.AsArray().Single()!["id"]!.GetValue<string>().ShouldBe(id);
            (await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/onlineMeetings?{Filter("https://elsewhere.example.com/call/abcd1234")}"))["value"]!.AsArray().ShouldBeEmpty();
            await SendAsync(alice, HttpMethod.Get, "/v1.0/me/onlineMeetings", expected: HttpStatusCode.BadRequest);

            // bob takes part, but not as organiser: /users/alice is alice's alone.
            using var bobClient = await live.Stack.GraphClientAsync("bob", Ct);
            await SendAsync(bobClient, HttpMethod.Get, $"/v1.0/users/{DevStack.Address("alice")}/onlineMeetings/{id}", expected: HttpStatusCode.Forbidden);

            var renamed = await SendAsync(alice, HttpMethod.Patch, $"/v1.0/me/onlineMeetings/{id}", new
            {
                subject = subject + " (moved)",
                startDateTime = start.AddDays(1).ToString("O", CultureInfo.InvariantCulture),
                endDateTime = start.AddDays(1).AddHours(1).ToString("O", CultureInfo.InvariantCulture),
            });
            // Talk renamed and re-timed the room from the meeting's event, within the write: still bound (ADR 0009 D7).
            renamed["subject"]!.GetValue<string>().ShouldBe(subject + " (moved)");
            renamed["startDateTime"]!.GetValue<DateTimeOffset>().ShouldBe(DateTimeOffset.FromUnixTimeSeconds(start.AddDays(1).ToUnixTimeSeconds()));
            await RoomShouldFollowAsync(live, token, subject + " (moved)", start.AddDays(1), start.AddDays(1).AddHours(1));
            (await EventsWithLinkAsync(live, token)).Count.ShouldBe(1);

            // A meeting made without a span can't gain one; it is renamed directly.
            var unbound = await SendAsync(alice, HttpMethod.Post, "/v1.0/me/onlineMeetings", new { subject = subject + " (no span)" }, HttpStatusCode.Created);
            var unboundId = unbound["id"]!.GetValue<string>();
            try
            {
                (await SendAsync(alice, HttpMethod.Patch, $"/v1.0/me/onlineMeetings/{unboundId}", new { subject = subject + " (renamed)" }))["subject"]!.GetValue<string>()
                    .ShouldBe(subject + " (renamed)");
                await SendAsync(alice, HttpMethod.Patch, $"/v1.0/me/onlineMeetings/{unboundId}", new
                {
                    startDateTime = start.ToString("O", CultureInfo.InvariantCulture),
                    endDateTime = start.AddHours(1).ToString("O", CultureInfo.InvariantCulture),
                }, HttpStatusCode.BadRequest);
            }
            finally
            {
                await SendAsync(alice, HttpMethod.Delete, $"/v1.0/me/onlineMeetings/{unboundId}", expected: HttpStatusCode.NoContent);
            }
        }
        finally
        {
            await SendAsync(alice, HttpMethod.Delete, $"/v1.0/me/onlineMeetings/{id}", expected: HttpStatusCode.NoContent);
        }

        await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/onlineMeetings/{id}", expected: HttpStatusCode.NotFound);
        (await EventsWithLinkAsync(live, token)).ShouldBeEmpty(); // the meeting's event went with it
    }

    [Fact]
    public async Task A_calendar_events_room_follows_the_event_when_it_is_renamed_and_moved()
    {
        Assert.SkipUnless(DevStack.Enabled, "Set NIGHTWARE_INTEGRATION=1 with the dev stack running.");
        await using var live = TalkLive.Start();
        using var alice = await live.Stack.GraphClientAsync("alice", Ct);
        var subject = "nw-event-follow-" + Guid.NewGuid().ToString("N")[..8];
        var start = DateTimeOffset.UtcNow.AddDays(3);
        var created = await SendAsync(alice, HttpMethod.Post, "/v1.0/me/events", new
        {
            subject,
            start = new { dateTime = Wall(start), timeZone = "UTC" },
            end = new { dateTime = Wall(start.AddHours(1)), timeZone = "UTC" },
            location = new { displayName = "Boardroom" },
            isOnlineMeeting = true,
        }, HttpStatusCode.Created);
        var eventId = created["id"]!.GetValue<string>();
        try
        {
            var joinUrl = created["onlineMeeting"]!["joinUrl"]!.GetValue<string>();
            var token = joinUrl[(joinUrl.LastIndexOf('/') + 1)..];

            // The link is in LOCATION for Talk, never in Graph's location.
            created["location"]!["displayName"]!.GetValue<string>().ShouldBe("Boardroom");
            var read = await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/events/{eventId}");
            read["location"]!["displayName"]!.GetValue<string>().ShouldBe("Boardroom");
            (await EventsWithLinkAsync(live, token)).Count.ShouldBe(1);

            var moved = start.AddDays(2);
            var patched = await SendAsync(alice, HttpMethod.Patch, $"/v1.0/me/events/{eventId}", new
            {
                subject = subject + " (moved)",
                start = new { dateTime = Wall(moved), timeZone = "UTC" },
                end = new { dateTime = Wall(moved.AddHours(2)), timeZone = "UTC" },
            });
            patched["location"]!["displayName"]!.GetValue<string>().ShouldBe("Boardroom");
            await RoomShouldFollowAsync(live, token, subject + " (moved)", moved, moved.AddHours(2));

            // A new place keeps the link after it; the room stays bound.
            (await SendAsync(alice, HttpMethod.Patch, $"/v1.0/me/events/{eventId}", new { location = new { displayName = "Room 2" } }))["location"]!["displayName"]!
                .GetValue<string>().ShouldBe("Room 2");
            await RoomShouldFollowAsync(live, token, subject + " (moved)", moved, moved.AddHours(2));

            // Through the meeting: the event is changed, and Talk follows it.
            var meetingId = "t." + token;
            var renamed = await SendAsync(alice, HttpMethod.Patch, $"/v1.0/me/onlineMeetings/{meetingId}", new { subject = subject + " (renamed)" });
            renamed["subject"]!.GetValue<string>().ShouldBe(subject + " (renamed)");
            (await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/events/{eventId}"))["subject"]!.GetValue<string>().ShouldBe(subject + " (renamed)");
            await RoomShouldFollowAsync(live, token, subject + " (renamed)", moved, moved.AddHours(2));
        }
        finally
        {
            await SendAsync(alice, HttpMethod.Delete, $"/v1.0/me/events/{eventId}", expected: HttpStatusCode.NoContent);
        }
    }

    [Fact]
    public async Task An_event_with_its_link_in_url_alone_gains_it_in_location_when_moved_and_the_room_follows()
    {
        Assert.SkipUnless(DevStack.Enabled, "Set NIGHTWARE_INTEGRATION=1 with the dev stack running.");
        await using var live = TalkLive.Start();
        using var alice = await live.Stack.GraphClientAsync("alice", Ct);
        var subject = "nw-legacy-" + Guid.NewGuid().ToString("N")[..8];
        var start = DateTimeOffset.UtcNow.AddDays(4);

        // As phase 22 made them: a room bound to the span and an event with the link in URL only.
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["roomType"] = "3", ["roomName"] = subject, ["objectType"] = "event",
            ["objectId"] = $"{start.ToUnixTimeSeconds()}#{start.AddHours(1).ToUnixTimeSeconds()}",
        });
        var token = await live.CreateRoomAsync("alice", form, Ct);
        var link = $"{DevStack.CloudBase}/call/{token}";
        var uid = "nw-legacy-" + Guid.NewGuid().ToString("N");
        var ics = string.Join("\r\n",
            "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//nightware-tests//EN", "BEGIN:VEVENT", $"UID:{uid}", "DTSTAMP:20261123T000000Z",
            $"DTSTART:{Ical(start)}", $"DTEND:{Ical(start.AddHours(1))}", $"SUMMARY:{subject}", "LOCATION:Boardroom", $"URL:{link}",
            "END:VEVENT", "END:VCALENDAR", string.Empty);
        using (var put = await live.DavAsync("alice", HttpMethod.Put, $"calendars/alice/personal/{uid}.ics", new StringContent(ics, Encoding.UTF8, "text/calendar"), Ct))
        {
            put.IsSuccessStatusCode.ShouldBeTrue();
        }

        try
        {
            var events = await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/events?$filter={Uri.EscapeDataString($"iCalUId eq '{uid}'")}");
            var eventId = events["value"]!.AsArray().Single()!["id"]!.GetValue<string>();
            var moved = start.AddDays(40); // past Talk's 28-day expiry of the old span: before, the room was unbound for this
            await SendAsync(alice, HttpMethod.Patch, $"/v1.0/me/events/{eventId}", new
            {
                start = new { dateTime = Wall(moved), timeZone = "UTC" },
                end = new { dateTime = Wall(moved.AddHours(1)), timeZone = "UTC" },
            });

            await RoomShouldFollowAsync(live, token, subject, moved, moved.AddHours(1));
            (await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/events/{eventId}"))["location"]!["displayName"]!.GetValue<string>().ShouldBe("Boardroom");
        }
        finally
        {
            using var _ = await live.DavAsync("alice", HttpMethod.Delete, $"calendars/alice/personal/{uid}.ics", null, Ct);
            await live.OcsAsync("alice", HttpMethod.Delete, $"apps/spreed/api/v4/room/{token}", Ct);
        }
    }

    private static string Wall(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    private static string Ical(DateTimeOffset at) => at.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>The room is still bound to an event (Talk expires it), named and timed as the event now is.</summary>
    private static async Task RoomShouldFollowAsync(TalkLive live, string token, string name, DateTimeOffset start, DateTimeOffset end)
    {
        var (_, room) = await live.OcsAsync("alice", HttpMethod.Get, $"apps/spreed/api/v4/room/{token}", Ct);
        room.GetProperty("objectType").GetString().ShouldBe("event");
        room.GetProperty("objectId").GetString().ShouldBe($"{start.ToUnixTimeSeconds()}#{end.ToUnixTimeSeconds()}");
        room.GetProperty("name").GetString().ShouldBe(name);
    }

    /// <summary>The hrefs of alice's personal events whose LOCATION holds the room's link (as Talk looks for them).</summary>
    private static async Task<List<string>> EventsWithLinkAsync(TalkLive live, string token)
    {
        var query = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <c:calendar-query xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav"><d:prop><d:getetag/></d:prop>
            <c:filter><c:comp-filter name="VCALENDAR"><c:comp-filter name="VEVENT"><c:prop-filter name="LOCATION"><c:text-match collation="i;octet">/call/{token}</c:text-match></c:prop-filter></c:comp-filter></c:comp-filter></c:filter></c:calendar-query>
            """;
        var xml = System.Xml.Linq.XDocument.Parse(await live.ReportAsync("alice", "calendars/alice/personal/", query, Ct));
        return [.. xml.Descendants(System.Xml.Linq.XName.Get("href", "DAV:")).Select(h => h.Value)];
    }

    [Fact]
    public async Task A_calendar_events_meeting_is_found_by_its_join_link()
    {
        Assert.SkipUnless(DevStack.Enabled, "Set NIGHTWARE_INTEGRATION=1 with the dev stack running.");
        await using var live = TalkLive.Start();
        using var alice = await live.Stack.GraphClientAsync("alice", Ct);
        var start = DateTimeOffset.UtcNow.AddDays(2);
        var created = await SendAsync(alice, HttpMethod.Post, "/v1.0/me/events", new
        {
            subject = "nw-event-meeting-" + Guid.NewGuid().ToString("N")[..8],
            start = new { dateTime = start.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), timeZone = "UTC" },
            end = new { dateTime = start.AddHours(1).ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), timeZone = "UTC" },
            isOnlineMeeting = true,
        }, HttpStatusCode.Created);
        try
        {
            var joinUrl = created["onlineMeeting"]!["joinUrl"]!.GetValue<string>();
            var found = await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/onlineMeetings?{Filter(joinUrl)}");
            var meeting = found["value"]!.AsArray().Single()!;
            meeting["joinWebUrl"]!.GetValue<string>().ShouldBe(joinUrl);
            meeting["endDateTime"]!.GetValue<DateTimeOffset>().ShouldBe(DateTimeOffset.FromUnixTimeSeconds(start.AddHours(1).ToUnixTimeSeconds()));

            // Graph's own example sends the link percent-encoded inside the literal.
            var encoded = await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/onlineMeetings?{Filter(Uri.EscapeDataString(joinUrl))}");
            encoded["value"]!.AsArray().Single()!["id"]!.GetValue<string>().ShouldBe(meeting["id"]!.GetValue<string>());
        }
        finally
        {
            await SendAsync(alice, HttpMethod.Delete, $"/v1.0/me/events/{created["id"]!.GetValue<string>()}", expected: HttpStatusCode.NoContent);
        }
    }

    [Fact]
    public async Task Recordings_and_transcripts_in_the_rooms_folder_list_and_download()
    {
        Assert.SkipUnless(DevStack.Enabled, "Set NIGHTWARE_INTEGRATION=1 with the dev stack running.");
        await using var live = TalkLive.Start();
        using var alice = await live.Stack.GraphClientAsync("alice", Ct);
        var meeting = await SendAsync(alice, HttpMethod.Post, "/v1.0/me/onlineMeetings", new { subject = "nw-recorded-" + Guid.NewGuid().ToString("N")[..8] }, HttpStatusCode.Created);
        var id = meeting["id"]!.GetValue<string>();
        var token = meeting["chatInfo"]!["threadId"]!.GetValue<string>();
        try
        {
            (await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/onlineMeetings/{id}/recordings"))["value"]!.AsArray().ShouldBeEmpty();

            // Where Talk's recording server and transcription put them (phase 26 Step 0), as they would.
            foreach (var folder in new[] { "Talk", "Talk/Recording", $"Talk/Recording/{token}" })
            {
                using var _ = await live.DavAsync("alice", new HttpMethod("MKCOL"), $"files/alice/{folder}", null, Ct);
            }

            var audio = Encoding.ASCII.GetBytes("OggS-not-really-audio");
            using (var put = await live.DavAsync("alice", HttpMethod.Put, $"files/alice/Talk/Recording/{token}/Recording%202026-11-23%2010-00-00.ogg",
                       new ByteArrayContent(audio) { Headers = { ContentType = new("audio/ogg") } }, Ct))
            {
                put.IsSuccessStatusCode.ShouldBeTrue();
            }

            using (var put = await live.DavAsync("alice", HttpMethod.Put, $"files/alice/Talk/Recording/{token}/Recording%202026-11-23%2010-00-00.md",
                       new StringContent("Good morning team.\n\nThe review moved to Thursday.\n\nTranscript is AI generated and may contain mistakes\n", Encoding.UTF8, "text/markdown"), Ct))
            {
                put.IsSuccessStatusCode.ShouldBeTrue();
            }

            var recordings = (await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/onlineMeetings/{id}/recordings"))["value"]!.AsArray();
            var recording = recordings.Single()!;
            recording["meetingId"]!.GetValue<string>().ShouldBe(id);
            recording["createdDateTime"]!.GetValue<DateTimeOffset>().ShouldBe(new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.Zero));
            var recordingId = recording["id"]!.GetValue<string>();
            (await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/onlineMeetings/{id}/recordings/{recordingId}"))["id"]!.GetValue<string>().ShouldBe(recordingId);
            using (var content = await alice.GetAsync(LiveStack.Local(recording["recordingContentUrl"]!.GetValue<string>()), Ct))
            {
                content.StatusCode.ShouldBe(HttpStatusCode.OK);
                content.Content.Headers.ContentType!.MediaType.ShouldBe("audio/ogg");
                (await content.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(audio);
            }

            var transcript = (await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/onlineMeetings/{id}/transcripts"))["value"]!.AsArray().Single()!;
            transcript["contentCorrelationId"]!.GetValue<string>().ShouldBe(recordingId);
            using (var vtt = await alice.GetAsync(LiveStack.Local(transcript["transcriptContentUrl"]!.GetValue<string>()) + "?$format=text/vtt", Ct))
            {
                vtt.StatusCode.ShouldBe(HttpStatusCode.OK);
                vtt.Content.Headers.ContentType!.MediaType.ShouldBe("text/vtt");
                var text = await vtt.Content.ReadAsStringAsync(Ct);
                text.ShouldStartWith("WEBVTT\n");
                text.ShouldContain("The review moved to Thursday.");
                text.ShouldNotContain("AI generated");
            }

            // A file id elsewhere in alice's home is no recording of this meeting.
            await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/onlineMeetings/{id}/recordings/{transcript["id"]!.GetValue<string>()}/content", expected: HttpStatusCode.NotFound);
        }
        finally
        {
            using var _ = await live.DavAsync("alice", HttpMethod.Delete, $"files/alice/Talk/Recording/{token}", null, Ct);
            await SendAsync(alice, HttpMethod.Delete, $"/v1.0/me/onlineMeetings/{id}", expected: HttpStatusCode.NoContent);
        }
    }
}

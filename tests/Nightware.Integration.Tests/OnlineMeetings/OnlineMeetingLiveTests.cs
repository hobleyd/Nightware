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
            renamed["subject"]!.GetValue<string>().ShouldBe(subject + " (moved)");
            renamed["startDateTime"].ShouldBeNull(); // unbound: Talk can't re-time a room
            var (_, room) = await live.OcsAsync("alice", HttpMethod.Get, $"apps/spreed/api/v4/room/{token}", Ct);
            room.GetProperty("objectType").GetString().ShouldBeEmpty();
        }
        finally
        {
            await SendAsync(alice, HttpMethod.Delete, $"/v1.0/me/onlineMeetings/{id}", expected: HttpStatusCode.NoContent);
        }

        await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/onlineMeetings/{id}", expected: HttpStatusCode.NotFound);
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

    /// <summary>
    /// The stack's <c>nightware_talk</c> app (deploy/nextcloud/apps) shares what lands in alice's recording folder with
    /// the conversation, so bob, who recorded nothing, lists and reads it with the same ids.
    /// </summary>
    [Fact]
    public async Task A_recording_shared_into_the_conversation_lists_and_downloads_for_every_participant()
    {
        Assert.SkipUnless(DevStack.Enabled, "Set NIGHTWARE_INTEGRATION=1 with the dev stack running.");
        await using var live = TalkLive.Start();
        using var alice = await live.Stack.GraphClientAsync("alice", Ct);
        using var bobClient = await live.Stack.GraphClientAsync("bob", Ct);
        var bobId = (await SendAsync(alice, HttpMethod.Get, $"/v1.0/users/{DevStack.Address("bob")}"))["id"]!.GetValue<string>();
        var meeting = await SendAsync(alice, HttpMethod.Post, "/v1.0/me/onlineMeetings", new
        {
            subject = "nw-shared-recording-" + Guid.NewGuid().ToString("N")[..8],
            participants = new { attendees = new[] { new { identity = new { user = new { id = bobId } } } } },
        }, HttpStatusCode.Created);
        var id = meeting["id"]!.GetValue<string>();
        var token = meeting["chatInfo"]!["threadId"]!.GetValue<string>();
        // A start of its own, so bob's earlier shares of the same name don't number this one.
        var start = new DateTimeOffset(2026, 11, 23, 0, 0, 0, TimeSpan.Zero).AddSeconds(Random.Shared.Next(0, 86400));
        var name = "Recording " + start.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture);
        try
        {
            foreach (var folder in new[] { "Talk", "Talk/Recording", $"Talk/Recording/{token}" })
            {
                using var _ = await live.DavAsync("alice", new HttpMethod("MKCOL"), $"files/alice/{folder}", null, Ct);
            }

            var audio = Encoding.ASCII.GetBytes("OggS-shared-" + token);
            using (var put = await live.DavAsync("alice", HttpMethod.Put, $"files/alice/Talk/Recording/{token}/{Uri.EscapeDataString(name)}.ogg",
                       new ByteArrayContent(audio) { Headers = { ContentType = new("audio/ogg") } }, Ct))
            {
                put.IsSuccessStatusCode.ShouldBeTrue();
            }

            using (var put = await live.DavAsync("alice", HttpMethod.Put, $"files/alice/Talk/Recording/{token}/{Uri.EscapeDataString(name)}.md",
                       new StringContent("Shared with the room.\n\nTranscript is AI generated and may contain mistakes\n", Encoding.UTF8, "text/markdown"), Ct))
            {
                put.IsSuccessStatusCode.ShouldBeTrue();
            }

            // The app posted each into the chat once, as alice.
            var (_, shared) = await live.OcsAsync("bob", HttpMethod.Get, $"apps/spreed/api/v1/chat/{token}/share/overview?limit=20", Ct);
            var posted = shared.EnumerateObject().SelectMany(c => c.Value.EnumerateArray()).ToList();
            posted.Select(m => m.GetProperty("messageParameters").GetProperty("file").GetProperty("name").GetString()).Order(StringComparer.Ordinal)
                .ShouldBe([name + ".md", name + ".ogg"]);
            posted.ShouldAllBe(m => m.GetProperty("actorId").GetString() == "alice");

            var aliceRecording = (await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/onlineMeetings/{id}/recordings"))["value"]!.AsArray().Single()!;
            var recording = (await SendAsync(bobClient, HttpMethod.Get, $"/v1.0/me/onlineMeetings/{id}/recordings"))["value"]!.AsArray().Single()!;
            var recordingId = recording["id"]!.GetValue<string>();
            recordingId.ShouldBe(aliceRecording["id"]!.GetValue<string>());
            recording["createdDateTime"]!.GetValue<DateTimeOffset>().ShouldBe(start);
            (await SendAsync(bobClient, HttpMethod.Get, $"/v1.0/me/onlineMeetings/{id}/recordings/{recordingId}"))["id"]!.GetValue<string>().ShouldBe(recordingId);
            using (var content = await bobClient.GetAsync(LiveStack.Local(recording["recordingContentUrl"]!.GetValue<string>()), Ct))
            {
                content.StatusCode.ShouldBe(HttpStatusCode.OK);
                content.Content.Headers.ContentType!.MediaType.ShouldBe("audio/ogg");
                (await content.Content.ReadAsByteArrayAsync(Ct)).ShouldBe(audio);
            }

            var transcript = (await SendAsync(bobClient, HttpMethod.Get, $"/v1.0/me/onlineMeetings/{id}/transcripts"))["value"]!.AsArray().Single()!;
            transcript["contentCorrelationId"]!.GetValue<string>().ShouldBe(recordingId);
            using (var vtt = await bobClient.GetAsync(LiveStack.Local(transcript["transcriptContentUrl"]!.GetValue<string>()), Ct))
            {
                vtt.StatusCode.ShouldBe(HttpStatusCode.OK);
                (await vtt.Content.ReadAsStringAsync(Ct)).ShouldContain("Shared with the room.");
            }
        }
        finally
        {
            using var _ = await live.DavAsync("alice", HttpMethod.Delete, $"files/alice/Talk/Recording/{token}", null, Ct);
            await SendAsync(alice, HttpMethod.Delete, $"/v1.0/me/onlineMeetings/{id}", expected: HttpStatusCode.NoContent);
        }
    }
}

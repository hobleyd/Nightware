using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;

using Nightware.Api.Tests.Support;
using Nightware.Application.Common.Identity;
using Nightware.Application.OnlineMeetings;
using Nightware.Domain.Kernel;
using Nightware.Domain.OnlineMeetings;

namespace Nightware.Api.Tests.Features.OnlineMeetings;

/// <summary>Phase 26: the online-meeting routes through the real composition, with the meeting ports faked (ADR 0009 D7).</summary>
public sealed class OnlineMeetingsEndpointsTests : IAsyncLifetime
{
    private const string Id = "t.abcd1234";
    private const string JoinUrl = "https://cloud.example.com/cloud/call/abcd1234";
    private static readonly DateTimeOffset Start = new(2026, 12, 1, 9, 0, 0, TimeSpan.Zero);

    private static readonly OnlineMeeting Meeting = new(Id, JoinUrl, "abcd1234")
    {
        Subject = "Q3 review",
        Start = Start,
        End = Start.AddHours(1),
        Created = Start.AddDays(-1),
        JoinMeetingId = "abcd1234",
        Organizer = new MeetingParticipant("me-id", "Me Example", "me@example.com", MeetingRole.Presenter),
        Attendees = [new MeetingParticipant("bob-id", "Bob Brown", "bob@example.com", MeetingRole.Attendee), new MeetingParticipant(null, "Guest", null, MeetingRole.CoOrganizer)],
    };

    private static readonly MeetingArtifact Recording = new("445", MeetingArtifactKind.Recording, Start, Start.AddHours(1)) { ContentCorrelationId = "445" };
    private static readonly MeetingArtifact Transcript = new("446", MeetingArtifactKind.Transcript, Start, Start.AddHours(1)) { ContentCorrelationId = "445" };

    private readonly IOnlineMeetingStore _store = Substitute.For<IOnlineMeetingStore>();
    private readonly IMeetingArtifacts _artifacts = Substitute.For<IMeetingArtifacts>();
    private readonly IMailboxResolver _mailboxes = Substitute.For<IMailboxResolver>();
    private TestHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient Client => _host.AuthorizedClient();

    public async ValueTask InitializeAsync()
    {
        _mailboxes.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(c => new MailboxRef(c.ArgAt<string?>(0) ?? TestHost.UserEmail));
        _store.GetAsync(Id, Arg.Any<CancellationToken>()).Returns(Meeting);
        _host = await TestHost.StartAsync(services =>
        {
            services.AddScoped(_ => _store);
            services.AddScoped(_ => _artifacts);
            services.AddScoped(_ => _mailboxes);
        });
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private async Task<JsonObject> SendAsync(HttpMethod method, string path, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        using var response = await Client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, text);
        return text.Length == 0 ? [] : JsonNode.Parse(text)!.AsObject();
    }

    [Theory]
    [InlineData("/v1.0/me")]
    [InlineData("/v1.0/users/me@example.com")]
    [InlineData("/beta/me")]
    public async Task A_meeting_reads_as_graphs_online_meeting(string at)
    {
        var meeting = await SendAsync(HttpMethod.Get, $"{at}/onlineMeetings/{Id}");

        meeting["id"]!.GetValue<string>().ShouldBe(Id);
        meeting["joinWebUrl"]!.GetValue<string>().ShouldBe(JoinUrl);
        meeting["startDateTime"]!.GetValue<string>().ShouldBe("2026-12-01T09:00:00Z");
        meeting["chatInfo"]!.ToJsonString().ShouldBe("""{"threadId":"abcd1234","messageId":"0","replyChainMessageId":null}""");
        meeting["joinMeetingIdSettings"]!.ToJsonString().ShouldBe("""{"isPasscodeRequired":false,"joinMeetingId":"abcd1234","passcode":null}""");
        meeting["participants"]!["organizer"]!.ToJsonString().ShouldBe("""{"identity":{"user":{"id":"me-id","displayName":"Me Example"}},"upn":"me@example.com","role":"presenter"}""");
        meeting["participants"]!["attendees"]![1]!.ToJsonString().ShouldBe("""{"identity":{"user":{"id":null,"displayName":"Guest"}},"upn":null,"role":"coorganizer"}""");
    }

    [Fact]
    public async Task Another_users_meetings_are_forbidden()
    {
        await SendAsync(HttpMethod.Get, $"/v1.0/users/bob@example.com/onlineMeetings/{Id}", expected: HttpStatusCode.Forbidden);
        await SendAsync(HttpMethod.Get, $"/v1.0/users/bob@example.com/onlineMeetings/{Id}/recordings", expected: HttpStatusCode.Forbidden);
        await SendAsync(HttpMethod.Post, "/v1.0/users/bob@example.com/onlineMeetings", new { subject = "x" }, HttpStatusCode.Forbidden);
        await _store.DidNotReceiveWithAnyArgs().CreateAsync(default!, Ct);
    }

    [Fact]
    public async Task Create_reads_the_subject_span_and_attendees()
    {
        _store.CreateAsync(Arg.Any<NewOnlineMeeting>(), Arg.Any<CancellationToken>()).Returns(Meeting);

        var created = await SendAsync(HttpMethod.Post, "/v1.0/me/onlineMeetings", new
        {
            subject = "Q3 review",
            startDateTime = "2026-12-01T19:00:00+10:00",
            endDateTime = "2026-12-01T20:00:00+10:00",
            participants = new { attendees = new object[] { new { identity = new { user = new { id = "bob-id" } } }, new { upn = "carol@example.com" } } },
        }, HttpStatusCode.Created);

        created["id"]!.GetValue<string>().ShouldBe(Id);
        await _store.Received().CreateAsync(
            Arg.Is<NewOnlineMeeting>(m => m.Subject == "Q3 review" && m.Start == Start && m.End == Start.AddHours(1) && m.Attendees.SequenceEqual(new[] { "bob-id", "carol@example.com" })),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("""{"startDateTime":"tomorrow"}""", "startDateTime")]
    [InlineData("""{"subject":5}""", "subject")]
    [InlineData("""{"participants":{"attendees":{}}}""", "participants")]
    [InlineData("""{"participants":{"attendees":[{"identity":{}}]}}""", "attendee")]
    [InlineData("""{"startDateTime":"2026-12-01T09:00:00Z"}""", "startDateTime")]
    public async Task Create_refuses_a_bad_body(string body, string target)
    {
        using var response = await Client.PostAsync("/v1.0/me/onlineMeetings", new StringContent(body, Encoding.UTF8, "application/json"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain(target);
    }

    [Fact]
    public async Task Find_answers_the_join_web_url_filter_only()
    {
        _store.FindByJoinUrlAsync(JoinUrl, Arg.Any<CancellationToken>()).Returns(Meeting);
        _store.FindByJoinUrlAsync("https://elsewhere.example.com/x", Arg.Any<CancellationToken>()).Returns(Error.NotFound("no"));

        var found = await SendAsync(HttpMethod.Get, "/v1.0/me/onlineMeetings?$filter=" + Uri.EscapeDataString($"JoinWebUrl eq '{JoinUrl}'"));
        found["value"]!.AsArray().Single()!["id"]!.GetValue<string>().ShouldBe(Id);
        (await SendAsync(HttpMethod.Get, "/v1.0/me/onlineMeetings?$filter=JoinWebUrl eq 'https://elsewhere.example.com/x'"))["value"]!.AsArray().ShouldBeEmpty();
        await SendAsync(HttpMethod.Get, "/v1.0/me/onlineMeetings", expected: HttpStatusCode.BadRequest);
        await SendAsync(HttpMethod.Get, "/v1.0/me/onlineMeetings?$filter=subject eq 'x'", expected: HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_and_delete_pass_through()
    {
        _store.UpdateAsync(Id, Arg.Any<OnlineMeetingChange>(), Arg.Any<CancellationToken>()).Returns(Meeting with { Subject = "New", Start = null, End = null });
        _store.DeleteAsync(Id, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _store.DeleteAsync("t.gone", Arg.Any<CancellationToken>()).Returns(Error.NotFound("gone"));

        var updated = await SendAsync(HttpMethod.Patch, $"/v1.0/me/onlineMeetings/{Id}", new { subject = "New", startDateTime = "2026-12-02T09:00:00Z", endDateTime = "2026-12-02T10:00:00Z" });
        updated["subject"]!.GetValue<string>().ShouldBe("New");
        updated["startDateTime"].ShouldBeNull();
        await _store.Received().UpdateAsync(Id, Arg.Is<OnlineMeetingChange>(c => c.ChangesSubject && c.Subject == "New" && c.Start == Start.AddDays(1)), Arg.Any<CancellationToken>());

        await SendAsync(HttpMethod.Patch, $"/v1.0/me/onlineMeetings/{Id}", new { meetingSpokenLanguageTag = "en-US" });
        await _store.Received().UpdateAsync(Id, new OnlineMeetingChange(false, null, null, null), Arg.Any<CancellationToken>());

        await SendAsync(HttpMethod.Delete, $"/v1.0/users/me@example.com/onlineMeetings/{Id}", expected: HttpStatusCode.NoContent);
        await SendAsync(HttpMethod.Delete, "/v1.0/me/onlineMeetings/t.gone", expected: HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Recordings_and_transcripts_list_with_their_content_urls()
    {
        _artifacts.ListAsync(Id, MeetingArtifactKind.Recording, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<MeetingArtifact>>.Success([Recording, Recording with { Id = "415" }]));
        _artifacts.ListAsync(Id, MeetingArtifactKind.Transcript, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<MeetingArtifact>>.Success([Transcript]));

        var recordings = (await SendAsync(HttpMethod.Get, $"/v1.0/me/onlineMeetings/{Id}/recordings"))["value"]!.AsArray();
        recordings.Count.ShouldBe(2);
        recordings[0]!["recordingContentUrl"]!.GetValue<string>().ShouldBe($"{TestHost.GraphBase}/me/onlineMeetings/{Id}/recordings/445/content");
        recordings[0]!["meetingOrganizer"]!.ToJsonString().ShouldBe("""{"application":null,"device":null,"user":{"id":"me-id","displayName":"Me Example","userIdentityType":"aadUser"}}""");
        recordings[0]!.AsObject().ContainsKey("transcriptContentUrl").ShouldBeFalse();
        (await SendAsync(HttpMethod.Get, $"/v1.0/me/onlineMeetings/{Id}/recordings?$top=1"))["value"]!.AsArray().Count.ShouldBe(1);
        await SendAsync(HttpMethod.Get, $"/v1.0/me/onlineMeetings/{Id}/recordings?$orderby=createdDateTime", expected: HttpStatusCode.BadRequest);

        var transcript = await SendAsync(HttpMethod.Get, $"/v1.0/users/me@example.com/onlineMeetings/{Id}/transcripts/446");
        transcript["transcriptContentUrl"]!.GetValue<string>().ShouldEndWith($"/me/onlineMeetings/{Id}/transcripts/446/content");
        transcript["contentCorrelationId"]!.GetValue<string>().ShouldBe("445");
        await SendAsync(HttpMethod.Get, $"/v1.0/me/onlineMeetings/{Id}/transcripts/999", expected: HttpStatusCode.NotFound);
        await SendAsync(HttpMethod.Get, $"/v1.0/me/onlineMeetings/{Id}/recordings/445");
    }

    [Fact]
    public async Task Content_streams_and_transcripts_take_format_vtt_only()
    {
        _artifacts.OpenAsync(Id, MeetingArtifactKind.Recording, "445", Arg.Any<CancellationToken>())
            .Returns(_ => new BlobContent("audio/ogg", 4, _ => Task.FromResult<Stream>(new MemoryStream("OggS"u8.ToArray()))) { FileName = "r.ogg" });
        _artifacts.OpenAsync(Id, MeetingArtifactKind.Transcript, "446", Arg.Any<CancellationToken>())
            .Returns(_ => new BlobContent("text/vtt", 7, _ => Task.FromResult<Stream>(new MemoryStream("WEBVTT\n"u8.ToArray()))));

        using (var recording = await Client.GetAsync($"/v1.0/me/onlineMeetings/{Id}/recordings/445/content", Ct))
        {
            recording.StatusCode.ShouldBe(HttpStatusCode.OK);
            recording.Content.Headers.ContentType!.MediaType.ShouldBe("audio/ogg");
            (await recording.Content.ReadAsStringAsync(Ct)).ShouldBe("OggS");
        }

        using (var vtt = await Client.GetAsync($"/v1.0/me/onlineMeetings/{Id}/transcripts/446/content?$format=text/vtt", Ct))
        {
            vtt.Content.Headers.ContentType!.MediaType.ShouldBe("text/vtt");
            (await vtt.Content.ReadAsStringAsync(Ct)).ShouldBe("WEBVTT\n");
        }

        await SendAsync(HttpMethod.Get, $"/v1.0/me/onlineMeetings/{Id}/transcripts/446/content?$format=application/json", expected: HttpStatusCode.BadRequest);
        await SendAsync(HttpMethod.Get, $"/v1.0/me/onlineMeetings/{Id}/recordings/445/content?$format=text/vtt", expected: HttpStatusCode.BadRequest);
        await SendAsync(HttpMethod.Get, $"/v1.0/users/bob@example.com/onlineMeetings/{Id}/recordings/445/content", expected: HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Without_talk_the_routes_answer_501()
    {
        _store.GetAsync("t.off", Arg.Any<CancellationToken>()).Returns(Error.Unsupported("off"));

        (await SendAsync(HttpMethod.Get, "/v1.0/me/onlineMeetings/t.off", expected: HttpStatusCode.NotImplemented))["error"]!["code"]!.GetValue<string>().ShouldBe("notImplemented");
    }
}

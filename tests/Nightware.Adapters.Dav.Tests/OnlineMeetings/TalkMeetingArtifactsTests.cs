using System.Text;

using Nightware.Adapters.Dav.Tests.Talk;
using Nightware.Application.OnlineMeetings;
using Nightware.Domain.Kernel;
using Nightware.Domain.OnlineMeetings;

using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Nightware.Adapters.Dav.Tests.OnlineMeetings;

/// <summary>
/// Recordings and transcripts in the room's folder, <c>Talk/Recording/{token}/</c>, as the dev stack's recording server
/// and transcription left them (phase 26 Step 0): an <c>.ogg</c> or <c>.webm</c>, the transcript <c>.md</c> beside it;
/// and those shared into the conversation (Talk's shared items, as the <c>nightware_talk</c> app shares them), which
/// every participant reads at their own path (<c>Talk/{name}</c>).
/// </summary>
public sealed class TalkMeetingArtifactsTests : IDisposable
{
    private const string Folder = TalkAdapterHarness.DavRoot + "files/alice/Talk/Recording/abcd1234/";
    private const string Capabilities = "/cloud/ocs/v2.php/cloud/capabilities";
    private const string Shared = TalkAdapterHarness.Chat + "abcd1234/share";
    private const string Shares = TalkAdapterHarness.DavRoot + "files/alice/Talk/";

    private readonly TalkAdapterHarness _talk = new();

    public TalkMeetingArtifactsTests()
    {
        _talk.Ocs("GET", Capabilities, 200, """{"capabilities":{"spreed":{"config":{"attachments":{"allowed":true,"folder":"/Talk"}}}}}""");
        _talk.Dav("PROPFIND", Folder, 207, Listing, depth: "1");
        Share(Shared, "[]", priority: 100);
    }

    public void Dispose() => _talk.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IMeetingArtifacts Artifacts => _talk.Get<IMeetingArtifacts>();

    private static string File(string name, int id, string type, string modified, long uploaded) => $"""
        <d:response><d:href>{Folder}{name}</d:href><d:propstat><d:prop>
        <oc:fileid>{id}</oc:fileid><d:getcontenttype>{type}</d:getcontenttype><d:getcontentlength>3581</d:getcontentlength>
        <d:getlastmodified>{modified}</d:getlastmodified><nc:upload_time>{uploaded}</nc:upload_time><d:resourcetype/>
        </d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
        """;

    private static readonly string Listing = $"""
        <?xml version="1.0"?>
        <d:multistatus xmlns:d="DAV:" xmlns:oc="http://owncloud.org/ns" xmlns:nc="http://nextcloud.org/ns">
        <d:response><d:href>{Folder}</d:href><d:propstat><d:prop><oc:fileid>442</oc:fileid><d:resourcetype><d:collection/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
        {File("Recording%202026-11-23%2007-00-43.ogg", 445, "audio/ogg", "Mon, 23 Nov 2026 07:01:02 GMT", 1795417262)}
        {File("Recording%202026-11-23%2007-00-43.md", 446, "text/markdown", "Mon, 23 Nov 2026 07:01:37 GMT", 1795417297)}
        {File("Recording%202026-11-23%2007-00-43%20-%20summary.md", 447, "text/markdown", "Mon, 23 Nov 2026 07:02:00 GMT", 1795417320)}
        {File("Recording%202026-11-22%2022-52-17.webm", 415, "video/webm", "Sun, 22 Nov 2026 22:52:34 GMT", 0)}
        {File("notes.txt", 450, "text/plain", "Sun, 22 Nov 2026 22:52:34 GMT", 0)}
        </d:multistatus>
        """;

    /// <summary>Talk's shared items of a category (an object keyed by message id; <c>[]</c> when there are none).</summary>
    private void Share(string path, string data, string? category = null, string? lastKnown = null, int priority = 1, int status = 200)
    {
        var request = Request.Create().WithPath(path).UsingGet();
        if (category is not null)
        {
            request = request.WithParam("objectType", category);
        }

        if (lastKnown is not null)
        {
            request = request.WithParam("lastKnownMessageId", lastKnown);
        }

        _talk.Server.Given(request).AtPriority(priority).RespondWith(Response.Create().WithStatusCode(status)
            .WithHeader("Content-Type", "application/json; charset=utf-8")
            .WithBody($$$"""{"ocs":{"meta":{"status":"{{{(status < 300 ? "ok" : "failure")}}}","statuscode":{{{status}}},"message":""},"data":{{{data}}}}}"""));
    }

    /// <summary>A shared file's message, as Talk lists it for the caller (<c>path</c> is the caller's).</summary>
    private static string Message(int messageId, int fileId, string name, string mimetype, string actor = "bob") => $$$"""
        "{{{messageId}}}":{"id":{{{messageId}}},"token":"abcd1234","actorType":"users","actorId":"{{{actor}}}","timestamp":1795417300,
        "message":"{file}","messageType":"comment","messageParameters":{"actor":{"type":"user","id":"{{{actor}}}","name":"{{{actor}}}"},
        "file":{"type":"file","id":"{{{fileId}}}","name":"{{{name}}}","size":"21","path":"Talk/{{{name}}}","mimetype":"{{{mimetype}}}","permissions":"1"
        """ + "}}}";

    /// <summary>The caller's view of a file shared with them, at <c>Talk/{name}</c>.</summary>
    private void SharedFile(string name, int id, string type, string modified)
    {
        var href = Shares + Uri.EscapeDataString(name);
        _talk.Dav("PROPFIND", Shares + name, 207, $"""
            <?xml version="1.0"?>
            <d:multistatus xmlns:d="DAV:" xmlns:oc="http://owncloud.org/ns" xmlns:nc="http://nextcloud.org/ns">
            <d:response><d:href>{href}</d:href><d:propstat><d:prop>
            <oc:fileid>{id}</oc:fileid><d:getcontenttype>{type}</d:getcontenttype><d:getcontentlength>21</d:getcontentlength>
            <d:getlastmodified>{modified}</d:getlastmodified><nc:upload_time>0</nc:upload_time><d:resourcetype/>
            </d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
            </d:multistatus>
            """, depth: "0");
    }

    /// <summary>Bob's recording (and its transcript and summary) shared into the chat; alice has none of her own.</summary>
    private void BobsRecordingShared()
    {
        _talk.Dav("PROPFIND", Folder, 404, string.Empty, depth: "1");
        Share(Shared, "{" + Message(301, 601, "Recording 2026-11-24 08-00-00.ogg", "audio/ogg") + "}", "audio");
        Share(Shared, "{" + Message(303, 603, "Recording 2026-11-24 08-00-00 - summary.md", "text/markdown") + ","
            + Message(302, 602, "Recording 2026-11-24 08-00-00.md", "text/markdown") + ","
            + Message(299, 599, "minutes.md", "text/markdown") + "}", "file");
        SharedFile("Recording 2026-11-24 08-00-00.ogg", 601, "audio/ogg", "Tue, 24 Nov 2026 08:30:05 GMT");
        SharedFile("Recording 2026-11-24 08-00-00.md", 602, "text/markdown", "Tue, 24 Nov 2026 08:31:00 GMT");
    }

    [Fact]
    public async Task Recordings_are_the_audio_and_video_files_newest_first()
    {
        var recordings = (await Artifacts.ListAsync("t.abcd1234", MeetingArtifactKind.Recording, Ct)).Value;

        recordings.Select(r => r.Id).ShouldBe(["445", "415"]);
        recordings[0].Created.ShouldBe(new DateTimeOffset(2026, 11, 23, 7, 0, 43, TimeSpan.Zero));
        recordings[0].End.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1795417262));
        recordings[0].ContentCorrelationId.ShouldBe("445");

        // Without an upload time, the last modification stands in.
        recordings[1].End.ShouldBe(new DateTimeOffset(2026, 11, 22, 22, 52, 34, TimeSpan.Zero));
    }

    [Fact]
    public async Task A_transcript_is_the_md_beside_a_recording_and_a_summary_is_not_one()
    {
        var transcripts = (await Artifacts.ListAsync("t.abcd1234", MeetingArtifactKind.Transcript, Ct)).Value;

        var transcript = transcripts.ShouldHaveSingleItem();
        transcript.Id.ShouldBe("446");
        transcript.Created.ShouldBe(new DateTimeOffset(2026, 11, 23, 7, 0, 43, TimeSpan.Zero));
        transcript.End.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1795417262));
        transcript.ContentCorrelationId.ShouldBe("445");
    }

    [Fact]
    public async Task A_meeting_never_recorded_has_none_and_an_outage_is_reported()
    {
        _talk.Dav("PROPFIND", TalkAdapterHarness.DavRoot + "files/alice/Talk/Recording/efgh5678/", 404, string.Empty, depth: "1");
        _talk.Dav("PROPFIND", TalkAdapterHarness.DavRoot + "files/alice/Talk/Recording/ijkl9012/", 503, string.Empty, depth: "1");

        (await Artifacts.ListAsync("t.efgh5678", MeetingArtifactKind.Recording, Ct)).Value.ShouldBeEmpty();
        (await Artifacts.ListAsync("t.ijkl9012", MeetingArtifactKind.Recording, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        (await Artifacts.ListAsync("nope", MeetingArtifactKind.Recording, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task The_attachment_folder_is_the_callers_talk_setting()
    {
        _talk.Server.Reset();
        _talk.Ocs("GET", Capabilities, 200, """{"capabilities":{"spreed":{"config":{"attachments":{"folder":"/Chats"}}}}}""");
        _talk.Dav("PROPFIND", TalkAdapterHarness.DavRoot + "files/alice/Chats/Recording/abcd1234/", 207, Listing, depth: "1");
        Share(Shared, "[]");

        (await Artifacts.ListAsync("t.abcd1234", MeetingArtifactKind.Recording, Ct)).Value.Count.ShouldBe(2);
    }

    // WireMock matches the decoded path.
    [Fact]
    public async Task A_recording_streams_as_stored()
    {
        _talk.Dav("GET", Folder + "Recording 2026-11-23 07-00-43.ogg", 200, "OggS", "audio/ogg");

        var content = (await Artifacts.OpenAsync("t.abcd1234", MeetingArtifactKind.Recording, "445", Ct)).Value;

        content.ContentType.ShouldBe("audio/ogg");
        content.FileName.ShouldBe("Recording 2026-11-23 07-00-43.ogg");
        await using var stream = await content.Open(Ct);
        using var reader = new StreamReader(stream);
        (await reader.ReadToEndAsync(Ct)).ShouldBe("OggS");
    }

    [Fact]
    public async Task A_transcript_is_served_as_webvtt()
    {
        _talk.Dav("GET", Folder + "Recording 2026-11-23 07-00-43.md", 200, "Good morning team.\n\nTranscript is AI generated and may contain mistakes\n", "text/markdown");

        var content = (await Artifacts.OpenAsync("t.abcd1234", MeetingArtifactKind.Transcript, "446", Ct)).Value;

        content.ContentType.ShouldBe("text/vtt");
        content.FileName.ShouldBe("Recording 2026-11-23 07-00-43.vtt");
        await using var stream = await content.Open(Ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        (await reader.ReadToEndAsync(Ct)).ShouldBe("WEBVTT\n\n00:00:00.000 --> 00:00:19.000\nGood morning team.\n");
    }

    [Theory]
    [InlineData(MeetingArtifactKind.Recording, "446")] // a transcript is no recording
    [InlineData(MeetingArtifactKind.Transcript, "445")]
    [InlineData(MeetingArtifactKind.Recording, "450")] // another file in the folder
    [InlineData(MeetingArtifactKind.Recording, "1748")] // any file elsewhere
    public async Task Only_the_meetings_own_artifacts_of_that_kind_open(MeetingArtifactKind kind, string id)
    {
        (await Artifacts.OpenAsync("t.abcd1234", kind, id, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _talk.Requests("GET").Where(e => e.RequestMessage!.Path.StartsWith(Folder, StringComparison.Ordinal)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_participant_lists_the_recordings_and_transcripts_shared_into_the_conversation()
    {
        BobsRecordingShared();

        var recording = (await Artifacts.ListAsync("t.abcd1234", MeetingArtifactKind.Recording, Ct)).Value.ShouldHaveSingleItem();
        recording.Id.ShouldBe("601");
        recording.Created.ShouldBe(new DateTimeOffset(2026, 11, 24, 8, 0, 0, TimeSpan.Zero));
        recording.End.ShouldBe(new DateTimeOffset(2026, 11, 24, 8, 30, 5, TimeSpan.Zero));

        // The summary and another Markdown file in the chat are no transcripts, and aren't even looked up.
        var transcript = (await Artifacts.ListAsync("t.abcd1234", MeetingArtifactKind.Transcript, Ct)).Value.ShouldHaveSingleItem();
        transcript.Id.ShouldBe("602");
        transcript.ContentCorrelationId.ShouldBe("601");
        transcript.End.ShouldBe(recording.End);
        _talk.Requests("PROPFIND").Select(e => e.RequestMessage!.Path).ShouldNotContain(p => p.Contains("summary", StringComparison.Ordinal) || p.Contains("minutes", StringComparison.Ordinal));

        // Every category a room share or Talk's own "Share to chat" can put them in.
        _talk.Requests("GET", Shared).Select(e => e.RequestMessage!.Query!["objectType"].Single()).Distinct().Order()
            .ShouldBe(["audio", "file", "media", "recording"]);
    }

    [Fact]
    public async Task A_shared_recording_reads_at_the_participants_own_path()
    {
        BobsRecordingShared();
        _talk.Dav("GET", Shares + "Recording 2026-11-24 08-00-00.ogg", 200, "OggS", "audio/ogg");
        _talk.Dav("GET", Shares + "Recording 2026-11-24 08-00-00.md", 200, "Morning.\n\nTranscript is AI generated and may contain mistakes\n", "text/markdown");

        var content = (await Artifacts.OpenAsync("t.abcd1234", MeetingArtifactKind.Recording, "601", Ct)).Value;
        content.FileName.ShouldBe("Recording 2026-11-24 08-00-00.ogg");
        await using (var stream = await content.Open(Ct))
        {
            using var reader = new StreamReader(stream);
            (await reader.ReadToEndAsync(Ct)).ShouldBe("OggS");
        }

        var vtt = (await Artifacts.OpenAsync("t.abcd1234", MeetingArtifactKind.Transcript, "602", Ct)).Value;
        vtt.ContentType.ShouldBe("text/vtt");
        (await Artifacts.OpenAsync("t.abcd1234", MeetingArtifactKind.Transcript, "603", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task The_recorders_own_files_list_once_whether_shared_or_not()
    {
        // Alice recorded: 445 and its transcript 446 are shared; 415 isn't yet.
        Share(Shared, "{" + Message(311, 445, "Recording 2026-11-23 07-00-43.ogg", "audio/ogg", "alice") + "}", "audio");
        Share(Shared, "{" + Message(312, 446, "Recording 2026-11-23 07-00-43.md", "text/markdown", "alice") + "}", "file");

        (await Artifacts.ListAsync("t.abcd1234", MeetingArtifactKind.Recording, Ct)).Value.Select(r => r.Id).ShouldBe(["445", "415"]);
        (await Artifacts.ListAsync("t.abcd1234", MeetingArtifactKind.Transcript, Ct)).Value.ShouldHaveSingleItem().Id.ShouldBe("446");
        _talk.Requests("PROPFIND").Where(e => e.RequestMessage!.Path.StartsWith(Shares + "Recording ", StringComparison.Ordinal)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_shared_file_renamed_on_arrival_still_pairs_with_its_recording()
    {
        // The participant already had files of these names where shares land: Nextcloud numbers the new ones.
        _talk.Dav("PROPFIND", Folder, 404, string.Empty, depth: "1");
        Share(Shared, "{" + Message(321, 701, "Recording 2026-11-24 08-00-00 (2).webm", "video/webm") + "}", "media");
        Share(Shared, "{" + Message(322, 702, "Recording 2026-11-24 08-00-00 (3).md", "text/markdown") + "}", "file");
        SharedFile("Recording 2026-11-24 08-00-00 (2).webm", 701, "video/webm", "Tue, 24 Nov 2026 08:30:05 GMT");
        SharedFile("Recording 2026-11-24 08-00-00 (3).md", 702, "text/markdown", "Tue, 24 Nov 2026 08:31:00 GMT");

        (await Artifacts.ListAsync("t.abcd1234", MeetingArtifactKind.Recording, Ct)).Value.ShouldHaveSingleItem().Created
            .ShouldBe(new DateTimeOffset(2026, 11, 24, 8, 0, 0, TimeSpan.Zero));
        (await Artifacts.ListAsync("t.abcd1234", MeetingArtifactKind.Transcript, Ct)).Value.ShouldHaveSingleItem().ContentCorrelationId.ShouldBe("701");
    }

    [Fact]
    public async Task Shared_audio_that_is_no_recording_is_left_out()
    {
        _talk.Dav("PROPFIND", Folder, 404, string.Empty, depth: "1");
        Share(Shared, "{" + Message(331, 801, "voice note.ogg", "audio/ogg") + "}", "audio");

        (await Artifacts.ListAsync("t.abcd1234", MeetingArtifactKind.Recording, Ct)).Value.ShouldBeEmpty();
        _talk.Requests("PROPFIND").Where(e => e.RequestMessage!.Headers!["Depth"].Single() == "0").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_shared_file_gone_from_the_participants_files_is_left_out()
    {
        _talk.Dav("PROPFIND", Folder, 404, string.Empty, depth: "1");
        Share(Shared, "{" + Message(341, 901, "Recording 2026-11-24 09-00-00.ogg", "audio/ogg") + "}", "audio");
        _talk.Dav("PROPFIND", Shares + "Recording 2026-11-24 09-00-00.ogg", 404, string.Empty, depth: "0");

        (await Artifacts.ListAsync("t.abcd1234", MeetingArtifactKind.Recording, Ct)).Value.ShouldBeEmpty();
    }

    [Fact]
    public async Task Shared_items_are_read_page_by_page()
    {
        _talk.Dav("PROPFIND", Folder, 404, string.Empty, depth: "1");
        // A full page of chat audio (newest first), then the recording on the next, older page.
        var size = Nightware.Adapters.Dav.OnlineMeetings.TalkMeetingArtifacts.SharedPageSize;
        var page = string.Join(",", Enumerable.Range(0, size).Select(i => Message(1200 - i, 5000 + i, $"voice {i}.ogg", "audio/ogg")));
        Share(Shared, "{" + page + "}", "audio", priority: 2);
        Share(Shared, "{" + Message(1000, 601, "Recording 2026-11-24 08-00-00.ogg", "audio/ogg") + "}", "audio", lastKnown: (1201 - size).ToString(System.Globalization.CultureInfo.InvariantCulture));
        SharedFile("Recording 2026-11-24 08-00-00.ogg", 601, "audio/ogg", "Tue, 24 Nov 2026 08:30:05 GMT");

        (await Artifacts.ListAsync("t.abcd1234", MeetingArtifactKind.Recording, Ct)).Value.ShouldHaveSingleItem().Id.ShouldBe("601");
    }

    [Fact]
    public async Task Talk_failing_to_list_shared_items_is_reported_and_a_conversation_left_has_none()
    {
        Share(Shared, "[]", "audio", status: 503);
        (await Artifacts.ListAsync("t.abcd1234", MeetingArtifactKind.Recording, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);

        _talk.Server.Reset();
        _talk.Ocs("GET", Capabilities, 200, """{"capabilities":{"spreed":{"config":{"attachments":{"folder":"/Talk"}}}}}""");
        _talk.Dav("PROPFIND", Folder, 207, Listing, depth: "1");
        Share(Shared, "[]", status: 404);
        (await Artifacts.ListAsync("t.abcd1234", MeetingArtifactKind.Recording, Ct)).Value.Count.ShouldBe(2);
    }

    [Fact]
    public void A_name_numbered_on_arrival_has_the_recordings_stem()
    {
        Nightware.Adapters.Dav.OnlineMeetings.TalkMeetingArtifacts.StemOf("Recording 2026-11-23 07-00-43 (2).ogg").ShouldBe("Recording 2026-11-23 07-00-43");
        Nightware.Adapters.Dav.OnlineMeetings.TalkMeetingArtifacts.StemOf("Recording 2026-11-23 07-00-43.md").ShouldBe("Recording 2026-11-23 07-00-43");
        Nightware.Adapters.Dav.OnlineMeetings.TalkMeetingArtifacts.StemOf("Notes (draft).md").ShouldBe("Notes (draft)");
    }

    [Fact]
    public void A_recording_name_gives_its_start()
    {
        Nightware.Adapters.Dav.OnlineMeetings.TalkMeetingArtifacts.StartOf("Recording 2026-11-23 07-00-43").ShouldBe(new DateTimeOffset(2026, 11, 23, 7, 0, 43, TimeSpan.Zero));
        Nightware.Adapters.Dav.OnlineMeetings.TalkMeetingArtifacts.StartOf("Recording 2026-11-23").ShouldBeNull();
        Nightware.Adapters.Dav.OnlineMeetings.TalkMeetingArtifacts.StartOf("Recording 2026-13-23 07-00-43").ShouldBeNull();
    }
}

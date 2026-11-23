using System.Text;

using Nightware.Adapters.Dav.Tests.Talk;
using Nightware.Application.OnlineMeetings;
using Nightware.Domain.Kernel;
using Nightware.Domain.OnlineMeetings;

namespace Nightware.Adapters.Dav.Tests.OnlineMeetings;

/// <summary>
/// Recordings and transcripts in the room's folder, <c>Talk/Recording/{token}/</c>, as the dev stack's recording server
/// and transcription left them (phase 26 Step 0): an <c>.ogg</c> or <c>.webm</c>, the transcript <c>.md</c> beside it.
/// </summary>
public sealed class TalkMeetingArtifactsTests : IDisposable
{
    private const string Folder = TalkAdapterHarness.DavRoot + "files/alice/Talk/Recording/abcd1234/";
    private const string Capabilities = "/cloud/ocs/v2.php/cloud/capabilities";

    private readonly TalkAdapterHarness _talk = new();

    public TalkMeetingArtifactsTests()
    {
        _talk.Ocs("GET", Capabilities, 200, """{"capabilities":{"spreed":{"config":{"attachments":{"allowed":true,"folder":"/Talk"}}}}}""");
        _talk.Dav("PROPFIND", Folder, 207, Listing, depth: "1");
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
    public void A_recording_name_gives_its_start()
    {
        Nightware.Adapters.Dav.OnlineMeetings.TalkMeetingArtifacts.StartOf("Recording 2026-11-23 07-00-43").ShouldBe(new DateTimeOffset(2026, 11, 23, 7, 0, 43, TimeSpan.Zero));
        Nightware.Adapters.Dav.OnlineMeetings.TalkMeetingArtifacts.StartOf("Recording 2026-11-23").ShouldBeNull();
        Nightware.Adapters.Dav.OnlineMeetings.TalkMeetingArtifacts.StartOf("Recording 2026-13-23 07-00-43").ShouldBeNull();
    }
}

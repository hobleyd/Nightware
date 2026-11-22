using System.Text;
using Nightware.Adapters.Dav.Calendar;
using Nightware.Adapters.Dav.Tasks.Attachments;
using Nightware.Application.Calendar.Calendars;
using Nightware.Domain.Kernel;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Nightware.Adapters.Dav.Tests.Calendar;

/// <summary>Phase 19: event attachments uploaded in chunks over Nextcloud's chunked upload (v1), and read back as $value.</summary>
public sealed class DavEventAttachmentUploadsTests : IDisposable
{
    private const string Uploads = "/remote.php/dav/uploads/alice/";
    private readonly CalendarAdapterHarness _h = new();
    private readonly DavEventAttachmentStore _store;
    private readonly DavEventAttachmentUploads _uploads;

    public DavEventAttachmentUploadsTests()
    {
        var files = new TaskAttachmentFiles(_h.Dav.Client, _h.Dav.Paths);
        _store = new DavEventAttachmentStore(_h.Resources, _h.Scopes, files, _h.Editor, _h.Dav.Client);
        _uploads = new DavEventAttachmentUploads(_h.Resources, _h.Scopes, files, _store, _h.Dav.Client, _h.Dav.Time);
        _h.ServeIcs(CalendarAdapterHarness.SeriesHref, CalendarAdapterHarness.Read("series-alice.ics"));
    }

    public void Dispose() => _h.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private EventId Event => _h.IdOf(CalendarAdapterHarness.SeriesHref);

    private void On(string method, string pathPattern, int status, string? body = null) =>
        _h.Dav.Server.Given(Request.Create().WithPath(new WildcardMatcher(pathPattern)).UsingMethod(method))
            .RespondWith(body is null ? Response.Create().WithStatusCode(status) : Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/xml").WithBody(body));

    /// <summary>The staging folder's listing: the folder, Nextcloud's virtual .file, and chunks of the given sizes.</summary>
    private static string Listing(string folder, params (string Name, long Size)[] chunks)
    {
        var responses = new StringBuilder($"<d:response><d:href>{folder}</d:href><d:propstat><d:prop/><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>");
        responses.Append(System.Globalization.CultureInfo.InvariantCulture, $"<d:response><d:href>{folder}.file</d:href><d:propstat><d:prop><d:getcontentlength>{chunks.Sum(c => c.Size)}</d:getcontentlength></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>");
        foreach (var (name, size) in chunks)
        {
            responses.Append(System.Globalization.CultureInfo.InvariantCulture, $"<d:response><d:href>{folder}{name}</d:href><d:propstat><d:prop><d:getcontentlength>{size}</d:getcontentlength></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>");
        }

        return $"""<?xml version="1.0"?><d:multistatus xmlns:d="DAV:">{responses}</d:multistatus>""";
    }

    private async Task<(string SessionId, string Folder)> BeginAsync(long size = 10)
    {
        On("MKCOL", Uploads + "*", 201);
        var state = (await _uploads.BeginAsync(CalendarAdapterHarness.AliceMailbox, Event, new EventUploadFile("big.bin", "application/pdf", size), Ct)).Value;
        var folder = _h.Requests("MKCOL").Single().RequestMessage!.Path;
        return (state.SessionId, folder);
    }

    private static MemoryStream Bytes(int count) => new(Enumerable.Repeat((byte)7, count).ToArray());

    [Fact]
    public async Task Begin_checks_the_event_and_opens_a_staging_folder_in_the_owners_uploads()
    {
        var (session, folder) = await BeginAsync();

        folder.ShouldStartWith(Uploads + "nw-");
        folder.ShouldEndWith("/");
        var token = EventUploadToken.Decode(session, _h.Dav.Time.GetUtcNow()).Value;
        (token.Mailbox, token.Name, token.Size, token.HomeHref).ShouldBe((CalendarAdapterHarness.Alice, "big.bin", 10L, "files/alice/"));
    }

    [Fact]
    public async Task Begin_on_a_missing_event_opens_nothing()
    {
        var missing = _h.IdOf(CalendarAdapterHarness.AlicePersonal + "gone.ics");
        _h.Serve("GET", CalendarAdapterHarness.AlicePersonal + "gone.ics", 404);

        var result = await _uploads.BeginAsync(CalendarAdapterHarness.AliceMailbox, missing, new EventUploadFile("a", "b", 10), Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _h.Requests("MKCOL").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_middle_chunk_is_stored_by_its_first_byte_and_asks_for_the_next()
    {
        var (session, folder) = await BeginAsync();
        On("PROPFIND", folder, 207, Listing(folder, ("000000000000000", 4)));
        On("PUT", folder + "*", 201);

        var progress = await _uploads.AppendAsync(CalendarAdapterHarness.AliceMailbox, Event, session, 4, 3, 10, Bytes(3), Ct);

        progress.Value.ShouldBe(new EventUploadProgress(7, null));
        _h.Requests("PUT").Single().RequestMessage!.Path.ShouldBe(folder + "000000000000004");
    }

    [Fact]
    public async Task A_chunk_that_isnt_the_next_byte_is_400_and_stores_nothing()
    {
        var (session, folder) = await BeginAsync();
        On("PROPFIND", folder, 207, Listing(folder, ("000000000000000", 4)));

        var result = await _uploads.AppendAsync(CalendarAdapterHarness.AliceMailbox, Event, session, 0, 4, 10, Bytes(4), Ct);

        result.Error!.Message.ShouldContain("4");
        _h.Requests("PUT").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_chunk_shorter_than_its_range_is_removed_and_400()
    {
        var (session, folder) = await BeginAsync();
        On("PROPFIND", folder, 207, Listing(folder));
        On("PUT", folder + "*", 201);
        On("DELETE", folder + "*", 204);

        var result = await _uploads.AppendAsync(CalendarAdapterHarness.AliceMailbox, Event, session, 0, 5, 10, Bytes(3), Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        _h.Requests("DELETE").Single().RequestMessage!.Path.ShouldBe(folder + "000000000000000");
    }

    [Fact]
    public async Task The_last_chunk_moves_the_joined_file_into_files_and_attaches_it()
    {
        var (session, folder) = await BeginAsync();
        On("PROPFIND", folder, 207, Listing(folder, ("000000000000000", 6)));
        On("PUT", folder + "*", 201);
        On("MKCOL", "/remote.php/dav/files/alice/*", 201);
        On("MOVE", folder + ".file", 201);
        _h.Serve("PUT", CalendarAdapterHarness.SeriesHref, 204);

        var progress = (await _uploads.AppendAsync(CalendarAdapterHarness.AliceMailbox, Event, session, 6, 4, 10, Bytes(4), Ct)).Value;

        progress.NextExpected.ShouldBe(10);
        progress.Completed!.Name.ShouldBe("big.bin");
        progress.Completed.Size.ShouldBe(10);
        var destination = CalendarAdapterHarness.Header(_h.Requests("MOVE").Single(), "Destination")!;
        destination.ShouldContain("/remote.php/dav/files/alice/Nightware/Event%20attachments/");
        destination.ShouldEndWith("/big.bin");
        CalendarAdapterHarness.BodyOf(_h.Requests("PUT").Single(e => e.RequestMessage!.Path == CalendarAdapterHarness.SeriesHref)).ShouldContain("ATTACH");
    }

    [Fact]
    public async Task A_failed_move_leaves_no_folder_behind()
    {
        var (session, folder) = await BeginAsync();
        On("PROPFIND", folder, 207, Listing(folder, ("000000000000000", 6)));
        On("PUT", folder + "*", 201);
        On("MKCOL", "/remote.php/dav/files/alice/*", 201);
        On("MOVE", folder + ".file", 507);
        On("DELETE", "/remote.php/dav/files/alice/*", 204);

        (await _uploads.AppendAsync(CalendarAdapterHarness.AliceMailbox, Event, session, 6, 4, 10, Bytes(4), Ct)).IsSuccess.ShouldBeFalse();

        _h.Requests("DELETE").ShouldHaveSingleItem().RequestMessage!.Path.ShouldStartWith("/remote.php/dav/files/alice/Nightware/Event attachments/");
    }

    [Fact]
    public async Task A_session_for_another_mailbox_event_or_size_or_an_expired_one_is_refused()
    {
        var (session, _) = await BeginAsync();
        var other = _h.IdOf(CalendarAdapterHarness.AlicePersonal + "other.ics");

        (await _uploads.AppendAsync(CalendarAdapterHarness.BobMailbox, Event, session, 0, 1, 10, Bytes(1), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await _uploads.AppendAsync(CalendarAdapterHarness.AliceMailbox, other, session, 0, 1, 10, Bytes(1), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await _uploads.AppendAsync(CalendarAdapterHarness.AliceMailbox, Event, session, 0, 1, 11, Bytes(1), Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await _uploads.AppendAsync(CalendarAdapterHarness.AliceMailbox, Event, "garbage!", 0, 1, 10, Bytes(1), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);

        _h.Dav.Time.Advance(DavEventAttachmentUploads.SessionLifetime + TimeSpan.FromSeconds(1));
        (await _uploads.AppendAsync(CalendarAdapterHarness.AliceMailbox, Event, session, 0, 1, 10, Bytes(1), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task A_vanished_staging_folder_is_404()
    {
        var (session, folder) = await BeginAsync();
        On("PROPFIND", folder, 404);

        (await _uploads.AppendAsync(CalendarAdapterHarness.AliceMailbox, Event, session, 0, 1, 10, Bytes(1), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public void A_token_that_names_folders_outside_uploads_or_files_is_refused()
    {
        var now = DateTimeOffset.UnixEpoch;
        var good = new EventUploadToken("a@b", "e", "n", "c", 1, "uploads/alice/nw-1/", "files/alice/", 100);

        EventUploadToken.Decode(good.Encode(), now).IsSuccess.ShouldBeTrue();
        EventUploadToken.Decode((good with { UploadHref = "files/alice/x/" }).Encode(), now).IsSuccess.ShouldBeFalse();
        EventUploadToken.Decode((good with { HomeHref = "uploads/alice/" }).Encode(), now).IsSuccess.ShouldBeFalse();
        EventUploadToken.Decode((good with { UploadHref = "uploads/../files/" }).Encode(), now).IsSuccess.ShouldBeFalse();
        EventUploadToken.Decode((good with { Size = EventUploadLimits.MaxBytes + 1 }).Encode(), now).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task An_attachments_value_streams_the_linked_file_and_others_are_404()
    {
        const string file = "/remote.php/dav/files/alice/Nightware/Event%20attachments/f1/notes.txt";
        var link = new Nightware.Adapters.Dav.Calendar.ICal.EventAttachmentLink(new Uri(_h.Dav.Server.Url + file), "notes.txt", "text/plain", 5);
        var ics = _h.Editor.AddAttachment(CalendarAdapterHarness.Read("series-alice.ics"), link).Value;
        _h.Dav.Server.ResetMappings();
        _h.ServeIcs(CalendarAdapterHarness.SeriesHref, ics);
        _h.Serve("GET", "/remote.php/dav/files/alice/Nightware/Event attachments/f1/notes.txt", 200, "hello", "text/plain");
        var id = (await _store.ListAsync(CalendarAdapterHarness.AliceMailbox, Event, Ct)).Value.ShouldHaveSingleItem().Id;

        var content = (await _store.GetContentAsync(CalendarAdapterHarness.AliceMailbox, Event, id, Ct)).Value;

        (content.ContentType, content.FileName).ShouldBe(("text/plain", "notes.txt"));
        using var reader = new StreamReader(await content.Open(Ct));
        (await reader.ReadToEndAsync(Ct)).ShouldBe("hello");
        (await _store.GetContentAsync(CalendarAdapterHarness.AliceMailbox, Event, new AttachmentId("not-linked"), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }
}

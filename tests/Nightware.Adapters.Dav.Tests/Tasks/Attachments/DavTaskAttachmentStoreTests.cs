using System.Text;
using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Core.Discovery;
using Nightware.Adapters.Dav.Tasks.Attachments;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Dav.Tests.Tasks.Attachments;

public sealed class DavTaskAttachmentStoreTests : IDisposable
{
    private const string FileHref = "files/alice/Nightware/Task%20attachments/5f0c/Quarterly%20numbers.eml";
    private readonly TaskAdapterHarness _h = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string AttachLine(string href = FileHref) =>
        $"ATTACH;FMTTYPE=message/rfc822;FILENAME=Quarterly numbers.eml;SIZE=245:{_h.Dav.Server.Url}/remote.php/dav/{href}\r\n";

    private static AttachmentId IdOf(string href) => new(DavItemId.Resource(href).ToString());

    [Fact]
    public async Task Lists_the_attachments_stored_in_nextcloud_files()
    {
        _h.ServeTask(TaskAdapterHarness.Ics(extra: AttachLine() + "ATTACH;FMTTYPE=application/pdf:https://elsewhere.example/f/1\r\n"));

        var attachments = (await _h.Attachments.ListAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), TaskAdapterHarness.TaskIdOf(), Ct)).Value;

        var attachment = attachments.ShouldHaveSingleItem();
        attachment.Id.ShouldBe(IdOf(FileHref));
        attachment.Name.ShouldBe("Quarterly numbers.eml");
        attachment.ContentType.ShouldBe("message/rfc822");
        attachment.Size.ShouldBe(245);
    }

    [Fact]
    public async Task A_task_without_attachments_has_none()
    {
        _h.ServeTask(TaskAdapterHarness.Ics());

        var attachments = (await _h.Attachments.ListAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), TaskAdapterHarness.TaskIdOf(), Ct)).Value;

        attachments.ShouldBeEmpty();
    }

    [Fact]
    public async Task Listing_a_missing_task_is_not_found()
    {
        _h.Respond("GET", TaskAdapterHarness.OpenTask, 404);

        var result = await _h.Attachments.ListAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), TaskAdapterHarness.TaskIdOf(), Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Streams_an_attachments_bytes()
    {
        _h.ServeTask(TaskAdapterHarness.Ics(extra: AttachLine()));
        _h.RespondUnderFiles("GET", 200, "From: alex@example.com\r\n\r\nNumbers.", ("Content-Type", "message/rfc822"));

        var blob = (await _h.Attachments.GetContentAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), TaskAdapterHarness.TaskIdOf(), IdOf(FileHref), Ct)).Value;

        blob.ContentType.ShouldBe("message/rfc822");
        blob.FileName.ShouldBe("Quarterly numbers.eml");
        await using var stream = await blob.Open(Ct);
        using var reader = new StreamReader(stream);
        (await reader.ReadToEndAsync(Ct)).ShouldBe("From: alex@example.com\r\n\r\nNumbers.");
    }

    [Fact]
    public async Task Serves_only_files_the_task_references()
    {
        _h.ServeTask(TaskAdapterHarness.Ics(extra: AttachLine()));

        var result = await _h.Attachments.GetContentAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), TaskAdapterHarness.TaskIdOf(), IdOf("files/alice/secret.txt"), Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _h.Requests("GET").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_vanished_file_is_not_found()
    {
        _h.ServeTask(TaskAdapterHarness.Ics(extra: AttachLine()));
        _h.RespondUnderFiles("GET", 404);

        var result = await _h.Attachments.GetContentAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), TaskAdapterHarness.TaskIdOf(), IdOf(FileHref), Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Attaches_inline_content_as_a_file_referenced_from_the_task()
    {
        _h.ServeTask(TaskAdapterHarness.Ics());
        _h.Respond("PUT", TaskAdapterHarness.OpenTask, 204);
        _h.RespondUnderFiles("MKCOL", 201);
        _h.RespondUnderFiles("PUT", 201);
        var bytes = Encoding.UTF8.GetBytes("From: alex@example.com\r\n\r\nNumbers.");

        var attachment = (await _h.Attachments.CreateAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), TaskAdapterHarness.TaskIdOf(),
            new NewAttachment("Quarterly numbers.eml", "message/rfc822", bytes), Ct)).Value;

        // WireMock reports decoded paths; what was sent was percent-encoded.
        var mkcols = _h.Requests("MKCOL").Select(e => Encoded(e.RequestMessage!.AbsolutePath)).ToList();
        mkcols.Count.ShouldBe(3);
        mkcols[0].ShouldBe("/remote.php/dav/files/alice/Nightware/");
        mkcols[1].ShouldBe("/remote.php/dav/files/alice/Nightware/Task%20attachments/");
        mkcols[2].ShouldMatch("^/remote.php/dav/files/alice/Nightware/Task%20attachments/[0-9a-f]{32}/$");
        var file = _h.Requests("PUT").Single(e => e.RequestMessage!.AbsolutePath.StartsWith(TaskAdapterHarness.FilesHome, StringComparison.Ordinal));
        var filePath = Encoded(file.RequestMessage!.AbsolutePath);
        filePath.ShouldBe(mkcols[2] + "Quarterly%20numbers.eml");
        TaskAdapterHarness.Header(file, "If-None-Match").ShouldBe("*");
        file.RequestMessage!.BodyAsBytes.ShouldBe(bytes);

        var task = _h.Requests("PUT").Single(e => e.RequestMessage!.AbsolutePath == TaskAdapterHarness.OpenTask);
        TaskAdapterHarness.Header(task, "If-Match").ShouldBe("\"etag-1\"");
        var ics = TaskAdapterHarness.BodyOf(task).Replace("\r\n ", string.Empty, StringComparison.Ordinal);
        ics.ShouldContain("FILENAME=Quarterly numbers.eml");
        ics.ShouldContain("SIZE=" + bytes.Length);
        ics.ShouldContain("FMTTYPE=message/rfc822");
        ics.ShouldContain(_h.Dav.Server.Url + filePath);

        attachment.Id.ShouldBe(IdOf(filePath["/remote.php/dav/".Length..]));
        attachment.Name.ShouldBe("Quarterly numbers.eml");
        attachment.Size.ShouldBe(bytes.Length);
        attachment.LastModifiedDateTime.ShouldBe(_h.Dav.Time.GetUtcNow());
    }

    [Fact]
    public async Task A_missing_task_writes_no_file()
    {
        _h.Respond("GET", TaskAdapterHarness.OpenTask, 404);

        var result = await _h.Attachments.CreateAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), TaskAdapterHarness.TaskIdOf(),
            new NewAttachment("a.eml", "message/rfc822", new byte[3]), Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _h.Requests("MKCOL").ShouldBeEmpty();
    }

    [Fact]
    public async Task Removes_the_file_when_the_task_cannot_be_updated()
    {
        _h.ServeTask(TaskAdapterHarness.Ics());
        _h.Respond("PUT", TaskAdapterHarness.OpenTask, 403);
        _h.RespondUnderFiles("MKCOL", 201);
        _h.RespondUnderFiles("PUT", 201);
        _h.RespondUnderFiles("DELETE", 204);

        var result = await _h.Attachments.CreateAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), TaskAdapterHarness.TaskIdOf(),
            new NewAttachment("a.eml", "message/rfc822", new byte[3]), Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        var delete = _h.Requests("DELETE").ShouldHaveSingleItem();
        delete.RequestMessage!.AbsolutePath.ShouldBe(_h.Requests("MKCOL")[2].RequestMessage!.AbsolutePath);
    }

    [Fact]
    public async Task File_write_failures_pass_through_and_leave_nothing_behind()
    {
        _h.ServeTask(TaskAdapterHarness.Ics());
        // Parents already exist (405); the attachment's own folder is new.
        _h.Dav.Server.Given(WireMock.RequestBuilders.Request.Create().WithPath(new WireMock.Matchers.WildcardMatcher(TaskAdapterHarness.FilesHome + "*")).UsingMethod("MKCOL"))
            .AtPriority(10).RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(405));
        _h.Dav.Server.Given(WireMock.RequestBuilders.Request.Create().WithPath(new WireMock.Matchers.RegexMatcher("^/remote.php/dav/files/alice/Nightware/Task attachments/[0-9a-f]{32}/$")).UsingMethod("MKCOL"))
            .AtPriority(1).RespondWith(WireMock.ResponseBuilders.Response.Create().WithStatusCode(201));
        _h.RespondUnderFiles("PUT", 507);
        _h.RespondUnderFiles("DELETE", 204);

        var result = await _h.Attachments.CreateAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), TaskAdapterHarness.TaskIdOf(),
            new NewAttachment("a.eml", "message/rfc822", new byte[3]), Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.PayloadTooLarge);
        _h.Requests("DELETE").ShouldHaveSingleItem();
        _h.Requests("PUT").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Folder_creation_failures_pass_through()
    {
        _h.ServeTask(TaskAdapterHarness.Ics());
        _h.RespondUnderFiles("MKCOL", 403);

        var result = await _h.Attachments.CreateAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), TaskAdapterHarness.TaskIdOf(),
            new NewAttachment("a.eml", "message/rfc822", new byte[3]), Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        _h.Requests("PUT").ShouldBeEmpty();
    }

    [Fact]
    public async Task Only_user_principals_hold_attachments()
    {
        using var h = new TaskAdapterHarness(TaskAdapterHarness.AlicePrincipal with { Href = "/remote.php/dav/principals/calendar-rooms/room-1/" });
        h.ServeTask(TaskAdapterHarness.Ics());

        var result = await h.Attachments.CreateAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), TaskAdapterHarness.TaskIdOf(),
            new NewAttachment("a.eml", "message/rfc822", new byte[3]), Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Unsupported);
    }

    [Fact]
    public async Task Opens_an_upload_session_for_an_existing_task()
    {
        _h.ServeTask(TaskAdapterHarness.Ics());

        var session = (await _h.Attachments.BeginUploadAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), TaskAdapterHarness.TaskIdOf(), "big.eml", "message/rfc822", 4096, Ct)).Value;

        session.TotalSize.ShouldBe(4096);
        session.ExpirationDateTime.ShouldBe(_h.Dav.Time.GetUtcNow() + DavTaskAttachmentStore.SessionLifetime);
        var token = TaskUploadToken.Decode(session.Token, _h.Dav.Time.GetUtcNow()).Value;
        token.ShouldBe(new TaskUploadToken(TaskAdapterHarness.Alice.Address, "calendars/alice/tasks/", "calendars/alice/tasks/0f1e2d3c4b5a69788796a5b4c3d2e1f0.ics",
            "big.eml", "message/rfc822", 4096, session.ExpirationDateTime.ToUnixTimeSeconds()));
    }

    [Fact]
    public async Task No_session_for_a_missing_task()
    {
        _h.Respond("GET", TaskAdapterHarness.OpenTask, 404);

        var result = await _h.Attachments.BeginUploadAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), TaskAdapterHarness.TaskIdOf(), "big.eml", "message/rfc822", 4096, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task No_session_for_a_task_outside_the_list()
    {
        var result = await _h.Attachments.BeginUploadAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), DavItemId.Resource("calendars/bob/tasks/x.ics").ToTaskId(), "big.eml", "message/rfc822", 4096, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _h.Dav.Server.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_whole_file_completes_the_session()
    {
        var session = await OpenSessionAsync(5000);
        _h.RespondUnderFiles("MKCOL", 201);
        _h.RespondUnderFiles("PUT", 201);
        _h.Respond("PUT", TaskAdapterHarness.OpenTask, 204);
        using var body = new MemoryStream(new byte[5000]);

        var progress = (await _h.Attachments.PutChunkAsync(session, 0, 4999, body, Ct)).Value;

        progress.NextExpectedRanges.ShouldBeEmpty();
        progress.Completed!.Name.ShouldBe("big.eml");
        progress.Completed.Size.ShouldBe(5000);
        _h.Requests("PUT").Single(e => e.RequestMessage!.AbsolutePath.StartsWith(TaskAdapterHarness.FilesHome, StringComparison.Ordinal))
            .RequestMessage!.BodyAsBytes!.Length.ShouldBe(5000);
    }

    [Fact]
    public async Task A_request_without_a_range_is_taken_as_the_whole_file()
    {
        var session = await OpenSessionAsync(5000);
        _h.RespondUnderFiles("MKCOL", 201);
        _h.RespondUnderFiles("PUT", 201);
        _h.Respond("PUT", TaskAdapterHarness.OpenTask, 204);
        using var body = new MemoryStream(new byte[5000]);

        var progress = await _h.Attachments.PutChunkAsync(session, 0, Application.Tasks.UploadTaskAttachmentContent.ToEnd, body, Ct);

        progress.Value.Completed!.Size.ShouldBe(5000);
    }

    [Theory]
    [InlineData(4999, ErrorKind.Validation)]
    [InlineData(5001, ErrorKind.PayloadTooLarge)]
    public async Task A_body_of_another_size_is_refused_and_removed(int length, ErrorKind kind)
    {
        var session = await OpenSessionAsync(5000);
        _h.RespondUnderFiles("MKCOL", 201);
        _h.RespondUnderFiles("PUT", 201);
        _h.RespondUnderFiles("DELETE", 204);
        using var body = new MemoryStream(new byte[length]);

        var result = await _h.Attachments.PutChunkAsync(session, 0, 4999, body, Ct);

        result.Error!.Kind.ShouldBe(kind);
        _h.Requests("DELETE").ShouldHaveSingleItem();
        _h.Requests("PUT").ShouldHaveSingleItem().RequestMessage!.BodyAsBytes!.Length.ShouldBeLessThanOrEqualTo(5000);
    }

    [Fact]
    public async Task Partial_ranges_are_refused()
    {
        var session = await OpenSessionAsync(5000);
        using var body = new MemoryStream(new byte[100]);

        var result = await _h.Attachments.PutChunkAsync(session, 0, 99, body, Ct);

        result.Error!.Target.ShouldBe("Content-Range");
    }

    [Fact]
    public async Task An_expired_session_is_not_found()
    {
        var session = await OpenSessionAsync(5000);
        _h.Dav.Time.Advance(DavTaskAttachmentStore.SessionLifetime + TimeSpan.FromSeconds(1));
        using var body = new MemoryStream(new byte[5000]);

        var result = await _h.Attachments.PutChunkAsync(session, 0, 4999, body, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task A_forged_session_is_not_found()
    {
        using var body = new MemoryStream(new byte[10]);

        var result = await _h.Attachments.PutChunkAsync(UploadSessionOf("bm90LWEtdG9rZW4"), 0, 9, body, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task A_session_naming_another_users_list_is_checked_like_any_request()
    {
        var token = new TaskUploadToken(TaskAdapterHarness.Alice.Address, "calendars/bob/tasks/", "calendars/bob/tasks/x.ics", "a", "text/plain", 10,
            (_h.Dav.Time.GetUtcNow() + TimeSpan.FromMinutes(5)).ToUnixTimeSeconds());
        using var body = new MemoryStream(new byte[10]);

        var result = await _h.Attachments.PutChunkAsync(new Domain.Tasks.UploadSession(token.Encode(), DateTimeOffset.MaxValue, 10), 0, 9, body, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _h.Dav.Server.LogEntries.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("big.eml", Application.Tasks.TaskAttachmentLimits.MaxBytes + 1)]
    [InlineData("big.eml", 0L)]
    [InlineData(" ", 10L)]
    [InlineData(null, 10L)]
    public async Task A_hand_built_session_beyond_the_attachment_limits_is_not_found(string? name, long size)
    {
        // What BeginTaskAttachmentUpload refuses must not get in through a token nobody minted.
        var token = new TaskUploadToken(TaskAdapterHarness.Alice.Address, "calendars/alice/tasks/", "calendars/alice/tasks/0f1e2d3c4b5a69788796a5b4c3d2e1f0.ics",
            name ?? new string('n', Application.Tasks.TaskAttachmentLimits.MaxNameLength + 1), "text/plain", size,
            (_h.Dav.Time.GetUtcNow() + TimeSpan.FromMinutes(5)).ToUnixTimeSeconds());
        using var body = new MemoryStream(new byte[10]);

        var result = await _h.Attachments.PutChunkAsync(new Domain.Tasks.UploadSession(token.Encode(), DateTimeOffset.MaxValue, size), 0, UploadTaskAttachmentContentToEnd, body, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _h.Dav.Server.LogEntries.ShouldBeEmpty();
    }

    private const long UploadTaskAttachmentContentToEnd = Application.Tasks.UploadTaskAttachmentContent.ToEnd;

    [Fact]
    public async Task Deleting_attachments_is_not_supported()
    {
        var result = await _h.Attachments.DeleteAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), TaskAdapterHarness.TaskIdOf(), IdOf(FileHref), Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Unsupported);
    }

    private async Task<Domain.Tasks.UploadSession> OpenSessionAsync(long size)
    {
        _h.ServeTask(TaskAdapterHarness.Ics());
        return (await _h.Attachments.BeginUploadAsync(TaskAdapterHarness.Alice, TaskAdapterHarness.ListId(), TaskAdapterHarness.TaskIdOf(), "big.eml", "message/rfc822", size, Ct)).Value;
    }

    private static string Encoded(string path) => path.Replace(" ", "%20", StringComparison.Ordinal);

    private static Domain.Tasks.UploadSession UploadSessionOf(string token) => new(token, DateTimeOffset.MaxValue, 10);

    public void Dispose() => _h.Dispose();
}

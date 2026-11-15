using Nightware.Application.Tasks;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;
using Nightware.Domain.Tasks;

namespace Nightware.Application.Tests.Tasks;

public sealed class TaskAttachmentUseCaseTests
{
    private static readonly MailboxRef Mailbox = new("alice@example.com");
    private static readonly TaskListId List = new("list-1");
    private static readonly TaskId Task1 = new("task-1");
    private static readonly TaskAttachment Stored = new(new AttachmentId("att-1"), "thread.eml", "message/rfc822", 245);
    private static readonly UploadSession Session = new("session-token", new DateTimeOffset(2026, 6, 1, 10, 21, 0, TimeSpan.Zero), 4096);

    private readonly ITaskAttachmentStore _store = Substitute.For<ITaskAttachmentStore>();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task List_comes_from_the_store()
    {
        _store.ListAsync(Mailbox, List, Task1, Ct).Returns(Result<IReadOnlyList<TaskAttachment>>.Success([Stored]));

        var result = await new ListTaskAttachments(_store).ExecuteAsync(Mailbox, List, Task1, Ct);

        result.Value.ShouldBe([Stored]);
    }

    [Fact]
    public async Task Content_comes_from_the_store()
    {
        var blob = new BlobContent("message/rfc822", 3, _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])));
        _store.GetContentAsync(Mailbox, List, Task1, Stored.Id, Ct).Returns(blob);

        var result = await new GetTaskAttachmentContent(_store).ExecuteAsync(Mailbox, List, Task1, Stored.Id, Ct);

        result.Value.ShouldBeSameAs(blob);
    }

    [Fact]
    public async Task Inline_attachments_are_stored()
    {
        var attachment = new NewAttachment("thread.eml", "message/rfc822", new byte[245]);
        _store.CreateAsync(Mailbox, List, Task1, attachment, Ct).Returns(Stored);

        var result = await new AddTaskAttachment(_store).ExecuteAsync(Mailbox, List, Task1, attachment, Ct);

        result.Value.ShouldBeSameAs(Stored);
    }

    [Fact]
    public async Task Inline_attachments_over_three_megabytes_are_refused()
    {
        var attachment = new NewAttachment("big.eml", "message/rfc822", new byte[TaskAttachmentLimits.MaxInlineBytes + 1]);

        var result = await new AddTaskAttachment(_store).ExecuteAsync(Mailbox, List, Task1, attachment, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.PayloadTooLarge);
        await _store.DidNotReceiveWithAnyArgs().CreateAsync(default, default, default, null!, Ct);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Inline_attachments_need_a_name(string name)
    {
        var result = await new AddTaskAttachment(_store).ExecuteAsync(Mailbox, List, Task1, new NewAttachment(name, "text/plain", new byte[1]), Ct);

        result.Error!.Target.ShouldBe("name");
    }

    [Fact]
    public async Task Overlong_names_are_refused()
    {
        var name = new string('a', TaskAttachmentLimits.MaxNameLength + 1);

        var result = await new BeginTaskAttachmentUpload(_store).ExecuteAsync(Mailbox, List, Task1, name, "text/plain", 10, Ct);

        result.Error!.Target.ShouldBe("name");
    }

    [Fact]
    public async Task An_upload_session_is_opened_with_the_declared_size()
    {
        _store.BeginUploadAsync(Mailbox, List, Task1, "thread.eml", "message/rfc822", 4096, Ct).Returns(Session);

        var result = await new BeginTaskAttachmentUpload(_store).ExecuteAsync(Mailbox, List, Task1, "thread.eml", "message/rfc822", 4096, Ct);

        result.Value.ShouldBeSameAs(Session);
    }

    [Fact]
    public async Task A_missing_content_type_defaults_to_octet_stream()
    {
        _store.BeginUploadAsync(Mailbox, List, Task1, "x.bin", "application/octet-stream", 10, Ct).Returns(Session);

        var result = await new BeginTaskAttachmentUpload(_store).ExecuteAsync(Mailbox, List, Task1, "x.bin", null, 10, Ct);

        result.IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0, ErrorKind.Validation)]
    [InlineData(-1, ErrorKind.Validation)]
    [InlineData(TaskAttachmentLimits.MaxBytes + 1, ErrorKind.PayloadTooLarge)]
    public async Task Upload_sizes_are_bounded(long size, ErrorKind kind)
    {
        var result = await new BeginTaskAttachmentUpload(_store).ExecuteAsync(Mailbox, List, Task1, "x.bin", "application/octet-stream", size, Ct);

        result.Error!.Kind.ShouldBe(kind);
        await _store.DidNotReceiveWithAnyArgs().BeginUploadAsync(default, default, default, null!, null!, default, Ct);
    }

    [Fact]
    public async Task The_whole_file_completes_the_upload()
    {
        using var body = new MemoryStream(new byte[4096]);
        _store.PutChunkAsync(Arg.Is<UploadSession>(s => s.Token == "session-token" && s.TotalSize == 4096), 0, 4095, body, Ct)
            .Returns(new UploadProgress([], Stored));

        var result = await new UploadTaskAttachmentContent(_store).ExecuteAsync("session-token", new ContentRange(0, 4095, 4096), body, Ct);

        result.Value.ShouldBeSameAs(Stored);
    }

    [Fact]
    public async Task A_request_without_content_range_is_the_whole_file()
    {
        using var body = new MemoryStream(new byte[4096]);
        _store.PutChunkAsync(Arg.Is<UploadSession>(s => s.Token == "session-token"), 0, UploadTaskAttachmentContent.ToEnd, body, Ct)
            .Returns(new UploadProgress([], Stored));

        var result = await new UploadTaskAttachmentContent(_store).ExecuteAsync("session-token", null, body, Ct);

        result.Value.ShouldBeSameAs(Stored);
    }

    [Theory]
    [InlineData(0, 1023, 4096)]
    [InlineData(1024, 4095, 4096)]
    [InlineData(0, -1, 0)]
    public async Task Partial_ranges_are_refused(long first, long last, long total)
    {
        using var body = new MemoryStream();

        var result = await new UploadTaskAttachmentContent(_store).ExecuteAsync("session-token", new ContentRange(first, last, total), body, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        result.Error.Target.ShouldBe("Content-Range");
        await _store.DidNotReceiveWithAnyArgs().PutChunkAsync(null!, default, default, null!, Ct);
    }

    [Fact]
    public async Task An_upload_the_store_did_not_complete_is_an_error()
    {
        using var body = new MemoryStream(new byte[4096]);
        _store.PutChunkAsync(Arg.Any<UploadSession>(), 0, UploadTaskAttachmentContent.ToEnd, body, Ct).Returns(new UploadProgress(["4096-"], null));

        var result = await new UploadTaskAttachmentContent(_store).ExecuteAsync("session-token", null, body, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Unknown);
    }

    [Fact]
    public async Task Store_failures_pass_through()
    {
        using var body = new MemoryStream(new byte[4096]);
        _store.PutChunkAsync(Arg.Any<UploadSession>(), 0, UploadTaskAttachmentContent.ToEnd, body, Ct).Returns(Error.Validation("size mismatch"));

        var result = await new UploadTaskAttachmentContent(_store).ExecuteAsync("session-token", null, body, Ct);

        result.Error!.Message.ShouldBe("size mismatch");
    }

}

using Nightware.Application.Mail;
using Nightware.Application.Mail.Write;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Tests.Mail.Write;

public sealed class DraftUploadUseCaseTests
{
    private static readonly MailboxRef Me = new("me@example.com");
    private static readonly MessageId Draft = new("d.abc");
    private static readonly DraftUploadFile File = new("big.bin", "application/octet-stream", 10);
    private readonly IDraftUploadStaging _staging = Substitute.For<IDraftUploadStaging>();
    private readonly IDraftStore _drafts = Substitute.For<IDraftStore>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Begin_normalises_the_file_and_opens_a_session()
    {
        _staging.BeginAsync(Me, Draft, Arg.Any<DraftUploadFile>(), Ct).Returns(new DraftUploadState("s1", File, 0));

        (await new BeginDraftAttachmentUpload(_staging).ExecuteAsync(Me, Draft, new DraftUploadFile(" big.bin ", "", 10), Ct)).Value.SessionId.ShouldBe("s1");

        await _staging.Received(1).BeginAsync(Me, Draft, File, Ct);
    }

    [Theory]
    [InlineData("", 10, ErrorKind.Validation)]
    [InlineData("a", 0, ErrorKind.Validation)]
    [InlineData("a", DraftUploadLimits.MaxBytes + 1, ErrorKind.PayloadTooLarge)]
    public async Task Begin_refuses_a_file_without_a_name_or_of_a_bad_size(string name, long size, ErrorKind kind)
    {
        (await new BeginDraftAttachmentUpload(_staging).ExecuteAsync(Me, Draft, new DraftUploadFile(name, "x/y", size), Ct)).Error!.Kind.ShouldBe(kind);
        await _staging.DidNotReceiveWithAnyArgs().BeginAsync(default, default, default!, Ct);
    }

    [Fact]
    public async Task A_middle_chunk_reports_the_next_byte_wanted()
    {
        using var chunk = new MemoryStream(new byte[4]);
        _staging.AppendAsync(Me, Draft, "s1", 0, 4, 10, chunk, Ct).Returns(new DraftUploadState("s1", File, 4));

        var progress = await new UploadDraftAttachmentChunk(_staging, _drafts).ExecuteAsync(Me, Draft, "s1", new ContentRangeHeader(0, 3, 10), chunk, Ct);

        progress.Value.ShouldBe(new DraftUploadProgress(4, null));
        await _drafts.DidNotReceiveWithAnyArgs().AddAttachmentAsync(default, default, default!, Ct);
    }

    [Fact]
    public async Task The_last_chunk_attaches_the_whole_file_to_the_draft()
    {
        using var chunk = new MemoryStream(new byte[6]);
        var inline = File with { IsInline = true, ContentId = "cid1" };
        _staging.AppendAsync(Me, Draft, "s1", 4, 6, 10, chunk, Ct).Returns(new DraftUploadState("s1", inline, 10));
        _staging.ReadAsync(Me, Draft, "s1", Ct).Returns(new ReadOnlyMemory<byte>(new byte[10]));
        var attachment = new MailAttachment(new AttachmentId("a1"), "big.bin", "application/octet-stream", 10, AttachmentKind.File);
        _drafts.AddAttachmentAsync(Me, Draft, Arg.Is<NewAttachment>(a => a.Name == "big.bin" && a.Content.Length == 10 && a.IsInline && a.ContentId == "cid1"), Ct)
            .Returns(attachment);

        var progress = await new UploadDraftAttachmentChunk(_staging, _drafts).ExecuteAsync(Me, Draft, "s1", new ContentRangeHeader(4, 9, 10), chunk, Ct);

        progress.Value.Completed.ShouldBe(attachment);
    }

    [Fact]
    public async Task A_malformed_range_never_reaches_the_staging_and_failures_are_returned()
    {
        using var chunk = new MemoryStream();
        (await new UploadDraftAttachmentChunk(_staging, _drafts).ExecuteAsync(Me, Draft, "s1", new ContentRangeHeader(5, 4, 10), chunk, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await new UploadDraftAttachmentChunk(_staging, _drafts).ExecuteAsync(Me, Draft, "s1", new ContentRangeHeader(0, 10, 10), chunk, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        await _staging.DidNotReceiveWithAnyArgs().AppendAsync(default, default, default!, default, default, default, default!, Ct);

        var boom = Error.NotFound("gone");
        _staging.AppendAsync(Me, Draft, "s1", 0, 1, 10, chunk, Ct).Returns(boom);
        (await new UploadDraftAttachmentChunk(_staging, _drafts).ExecuteAsync(Me, Draft, "s1", new ContentRangeHeader(0, 0, 10), chunk, Ct)).Error.ShouldBe(boom);
    }
}

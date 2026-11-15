using Nightware.Application.Common.Query;
using Nightware.Application.Mail;
using Nightware.Application.Mail.Read;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Tests.Mail.Read;

/// <summary>The pass-through read use cases: each forwards to its port and returns the port's answer unchanged.</summary>
public sealed class MailReadUseCaseTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly MailboxRef Mailbox = new("alice@example.com");
    private static readonly MessageId Id = new("e.m1");
    private static readonly AttachmentId Part = new("2");
    private static readonly FolderRef Inbox = new FolderRef.WellKnown(WellKnownFolder.Inbox);

    private readonly IMailFolderReader _folders = Substitute.For<IMailFolderReader>();
    private readonly IMailMessageReader _messages = Substitute.For<IMailMessageReader>();
    private readonly IMailDeltaReader _delta = Substitute.For<IMailDeltaReader>();

    private static BlobContent Blob() => new("message/rfc822", 1, _ => Task.FromResult<Stream>(new MemoryStream([1])));

    [Fact]
    public async Task List_mail_folders_asks_the_port_for_the_parent()
    {
        var page = new Page<MailFolder>([new MailFolder(new FolderId("a"), "Inbox", null, 0, 0, 0)]);
        _folders.ListAsync(Mailbox, Inbox, ItemQuery.Default, Ct).Returns(page);

        (await new ListMailFolders(_folders).ExecuteAsync(Mailbox, Inbox, ItemQuery.Default, Ct)).Value.ShouldBeSameAs(page);
    }

    [Fact]
    public async Task Get_mail_folder_passes_failures_through()
    {
        _folders.GetAsync(Mailbox, Inbox, Ct).Returns(Error.Forbidden("no"));

        (await new GetMailFolder(_folders).ExecuteAsync(Mailbox, Inbox, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task List_messages_forwards_the_query()
    {
        var query = new ItemQuery { Top = 25, Skip = 0 };
        _messages.ListAsync(Mailbox, null, query, Ct).Returns(Page<MailMessage>.Empty);

        (await new ListMessages(_messages).ExecuteAsync(Mailbox, null, query, Ct)).Value.ShouldBeSameAs(Page<MailMessage>.Empty);
    }

    [Fact]
    public async Task List_messages_rejects_expand_without_asking_the_port()
    {
        var query = new ItemQuery { Expand = [new ExpandClause("attachments")] };

        (await new ListMessages(_messages).ExecuteAsync(Mailbox, Inbox, query, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        await _messages.DidNotReceiveWithAnyArgs().ListAsync(default, default, default!, Ct);
    }

    [Fact]
    public async Task Sync_messages_forwards_the_token_and_page_size()
    {
        var token = new DeltaToken("t");
        var request = new DeltaRequest(50);
        var page = DeltaPage<MailMessage>.Done([], new DeltaToken("t2"));
        _delta.DeltaAsync(Mailbox, Inbox, token, request, Ct).Returns(page);

        (await new SyncMessages(_delta).ExecuteAsync(Mailbox, Inbox, token, request, Ct)).Value.ShouldBeSameAs(page);
    }

    [Fact]
    public async Task Sync_messages_rejects_a_non_positive_page_size()
    {
        (await new SyncMessages(_delta).ExecuteAsync(Mailbox, Inbox, null, new DeltaRequest(0), Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        await _delta.DidNotReceiveWithAnyArgs().DeltaAsync(default, default!, default, default!, Ct);
    }

    [Fact]
    public async Task Get_message_mime_reads_the_raw_message()
    {
        var blob = Blob();
        _messages.GetRawAsync(Mailbox, Id, Ct).Returns(blob);

        (await new GetMessageMime(_messages).ExecuteAsync(Mailbox, Id, Ct)).Value.ShouldBeSameAs(blob);
    }

    [Fact]
    public async Task List_message_attachments_reads_the_list()
    {
        IReadOnlyList<MailAttachment> attachments = [new MailAttachment(Part, "a.pdf", "application/pdf", 3, AttachmentKind.File)];
        _messages.ListAttachmentsAsync(Mailbox, Id, Ct).Returns(Result<IReadOnlyList<MailAttachment>>.Success(attachments));

        (await new ListMessageAttachments(_messages).ExecuteAsync(Mailbox, Id, Ct)).Value.ShouldBeSameAs(attachments);
    }

    [Fact]
    public async Task Get_message_attachment_always_asks_for_the_content()
    {
        var attachment = new MailAttachment(Part, "a.pdf", "application/pdf", 3, AttachmentKind.File) { ContentBytes = new byte[] { 1 } };
        _messages.GetAttachmentAsync(Mailbox, Id, Part, true, Ct).Returns(attachment);

        (await new GetMessageAttachment(_messages).ExecuteAsync(Mailbox, Id, Part, Ct)).Value.ShouldBeSameAs(attachment);
    }

    [Fact]
    public async Task Get_message_attachment_content_streams_the_part()
    {
        var blob = Blob();
        _messages.GetAttachmentContentAsync(Mailbox, Id, Part, Ct).Returns(blob);

        (await new GetMessageAttachmentContent(_messages).ExecuteAsync(Mailbox, Id, Part, Ct)).Value.ShouldBeSameAs(blob);
    }

    [Fact]
    public async Task Unconfigured_ports_never_pass_as_success()
    {
        (await new GetMessageMime(Substitute.For<IMailMessageReader>()).ExecuteAsync(Mailbox, Id, Ct)).IsSuccess.ShouldBeFalse();
    }
}

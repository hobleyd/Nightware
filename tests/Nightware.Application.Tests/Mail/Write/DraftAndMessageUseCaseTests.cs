using Nightware.Application.Mail;
using Nightware.Application.Mail.Write;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Tests.Mail.Write;

public sealed class DraftAndMessageUseCaseTests
{
    private static readonly MailboxRef Me = new("me@example.com");
    private static readonly MessageId Id = new("e.1");
    private static readonly MessageId DraftId = new("d.abc");
    private static readonly Error Boom = Error.NotFound("gone");

    private static readonly DraftContent Content = new()
    {
        Subject = "Hello",
        Body = new ItemBody(BodyType.Html, "<p>hi</p>"),
        ToRecipients = [new EmailAddress("to@example.com")],
        CcRecipients = [new EmailAddress("cc@example.com")],
        BccRecipients = [new EmailAddress("bcc@example.com")],
        Importance = Importance.High,
        Attachments = [new NewAttachment("a.txt", "text/plain", new byte[] { 1 })],
    };

    private readonly IMailMessageWriter _writer = Substitute.For<IMailMessageWriter>();
    private readonly IDraftStore _drafts = Substitute.For<IDraftStore>();
    private readonly IMailMessageReader _reader = Substitute.For<IMailMessageReader>();
    private readonly IMailImporter _importer = Substitute.For<IMailImporter>();
    private readonly IMailSender _sender = Substitute.For<IMailSender>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_draft_returns_the_reread_draft()
    {
        var draft = new MailMessage(DraftId) { IsDraft = true };
        _drafts.CreateAsync(Me, Content, Arg.Any<CancellationToken>()).Returns(DraftId);
        _reader.GetAsync(Me, DraftId, MessageFetch.Default, Arg.Any<CancellationToken>()).Returns(draft);

        (await new CreateDraft(_drafts, _reader).ExecuteAsync(Me, Content, Ct)).Value.ShouldBe(draft);
    }

    [Fact]
    public async Task Create_draft_answers_with_the_draft_as_written_when_the_reread_fails()
    {
        _drafts.CreateAsync(Me, Content, Arg.Any<CancellationToken>()).Returns(DraftId);
        _reader.GetAsync(Me, DraftId, MessageFetch.Default, Arg.Any<CancellationToken>()).Returns(Boom);

        var written = (await new CreateDraft(_drafts, _reader).ExecuteAsync(Me, Content, Ct)).Value;

        written.Id.ShouldBe(DraftId);
        written.IsDraft.ShouldBeTrue();
        written.IsRead.ShouldBeTrue();
        written.Subject.ShouldBe("Hello");
        written.Body.ShouldBe(Content.Body);
        written.ToRecipients.ShouldBe(Content.ToRecipients);
        written.CcRecipients.ShouldBe(Content.CcRecipients);
        written.BccRecipients.ShouldBe(Content.BccRecipients);
        written.Importance.ShouldBe(Importance.High);
        written.HasAttachments.ShouldBeTrue();
    }

    [Fact]
    public async Task Create_draft_failure_is_returned_without_a_read()
    {
        _drafts.CreateAsync(Me, Content, Arg.Any<CancellationToken>()).Returns(Boom);

        (await new CreateDraft(_drafts, _reader).ExecuteAsync(Me, Content, Ct)).Error.ShouldBe(Boom);
        await _reader.DidNotReceiveWithAnyArgs().GetAsync(default, default, default!, Ct);
    }

    [Fact]
    public async Task Move_rereads_the_message_under_the_returned_id()
    {
        var destination = new FolderRef.WellKnown(WellKnownFolder.DeletedItems);
        var moved = new MailMessage(Id);
        _writer.MoveAsync(Me, Id, destination, Arg.Any<CancellationToken>()).Returns(Id);
        _reader.GetAsync(Me, Id, MessageFetch.Default, Arg.Any<CancellationToken>()).Returns(moved);

        (await new MoveMessage(_writer, _reader).ExecuteAsync(Me, Id, destination, Ct)).Value.ShouldBe(moved);
    }

    [Fact]
    public async Task Move_failures_are_returned()
    {
        var destination = new FolderRef.ById(new FolderId("f"));
        _writer.MoveAsync(Me, Id, destination, Arg.Any<CancellationToken>()).Returns(Boom);

        (await new MoveMessage(_writer, _reader).ExecuteAsync(Me, Id, destination, Ct)).Error.ShouldBe(Boom);

        _writer.MoveAsync(Me, Id, destination, Arg.Any<CancellationToken>()).Returns(Id);
        _reader.GetAsync(Me, Id, MessageFetch.Default, Arg.Any<CancellationToken>()).Returns(Boom);
        (await new MoveMessage(_writer, _reader).ExecuteAsync(Me, Id, destination, Ct)).Error.ShouldBe(Boom);
    }

    [Fact]
    public async Task Delete_and_permanent_delete_pass_through()
    {
        _writer.SoftDeleteAsync(Me, Id, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _writer.PermanentDeleteAsync(Me, Id, Arg.Any<CancellationToken>()).Returns(Result.Failure(Boom));

        (await new DeleteMessage(_writer).ExecuteAsync(Me, Id, Ct)).IsSuccess.ShouldBeTrue();
        (await new PermanentlyDeleteMessage(_writer).ExecuteAsync(Me, Id, Ct)).Error.ShouldBe(Boom);
    }

    [Fact]
    public async Task Import_rereads_the_imported_message()
    {
        var folder = new FolderRef.ById(new FolderId("f"));
        var options = new ImportOptions(IsRead: true, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        var imported = new MailMessage(Id);
        _importer.ImportAsync(Me, folder, Content, options, Arg.Any<CancellationToken>()).Returns(Id);
        _reader.GetAsync(Me, Id, MessageFetch.Default, Arg.Any<CancellationToken>()).Returns(imported);

        (await new ImportMessage(_importer, _reader).ExecuteAsync(Me, folder, Content, options, Ct)).Value.ShouldBe(imported);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Import_falls_back_to_the_message_as_written(bool isDraft)
    {
        var folder = new FolderRef.WellKnown(WellKnownFolder.Inbox);
        var options = new ImportOptions(IsRead: true, null, null, isDraft);
        _importer.ImportAsync(Me, folder, Content, options, Arg.Any<CancellationToken>()).Returns(Id);
        _reader.GetAsync(Me, Id, MessageFetch.Default, Arg.Any<CancellationToken>()).Returns(Boom);

        var written = (await new ImportMessage(_importer, _reader).ExecuteAsync(Me, folder, Content, options, Ct)).Value;

        written.Id.ShouldBe(Id);
        written.IsDraft.ShouldBe(isDraft);
        written.Subject.ShouldBe("Hello");
    }

    [Fact]
    public async Task Import_failure_is_returned()
    {
        var folder = new FolderRef.WellKnown(WellKnownFolder.Inbox);
        var options = new ImportOptions(false, null, null);
        _importer.ImportAsync(Me, folder, Content, options, Arg.Any<CancellationToken>()).Returns(Boom);

        (await new ImportMessage(_importer, _reader).ExecuteAsync(Me, folder, Content, options, Ct)).Error.ShouldBe(Boom);
        await _reader.DidNotReceiveWithAnyArgs().GetAsync(default, default, default!, Ct);
    }

    [Fact]
    public async Task Add_attachment_passes_through()
    {
        var attachment = new NewAttachment("notes.txt", "text/plain", new byte[] { 1, 2 });
        var added = new MailAttachment(new AttachmentId("3"), "notes.txt", "text/plain", 2, AttachmentKind.File);
        _drafts.AddAttachmentAsync(Me, DraftId, attachment, Arg.Any<CancellationToken>()).Returns(added);

        (await new AddDraftAttachment(_drafts).ExecuteAsync(Me, DraftId, attachment, Ct)).Value.ShouldBe(added);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Add_attachment_needs_a_name(string name)
    {
        var result = await new AddDraftAttachment(_drafts).ExecuteAsync(Me, DraftId, new NewAttachment(name, "text/plain", Array.Empty<byte>()), Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        result.Error.Target.ShouldBe("name");
        await _drafts.DidNotReceiveWithAnyArgs().AddAttachmentAsync(default, default, default!, Ct);
    }

    [Fact]
    public async Task Remove_attachment_passes_through()
    {
        var attachment = new AttachmentId("3");
        _drafts.RemoveAttachmentAsync(Me, DraftId, attachment, Arg.Any<CancellationToken>()).Returns(Result.Success());

        (await new RemoveDraftAttachment(_drafts).ExecuteAsync(Me, DraftId, attachment, Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Send_draft_and_send_mail_pass_through()
    {
        _sender.SendDraftAsync(Me, DraftId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _sender.SendAsync(Me, Content, false, Arg.Any<CancellationToken>()).Returns(Result.Failure(Boom));

        (await new SendDraft(_sender).ExecuteAsync(Me, DraftId, Ct)).IsSuccess.ShouldBeTrue();
        (await new SendMail(_sender).ExecuteAsync(Me, Content, saveToSentItems: false, Ct)).Error.ShouldBe(Boom);
    }

    [Fact]
    public async Task Use_cases_reject_null_inputs()
    {
        await Should.ThrowAsync<ArgumentNullException>(() => new CreateDraft(_drafts, _reader).ExecuteAsync(Me, null!, Ct));
        await Should.ThrowAsync<ArgumentNullException>(() => new MoveMessage(_writer, _reader).ExecuteAsync(Me, Id, null!, Ct));
        await Should.ThrowAsync<ArgumentNullException>(() => new ImportMessage(_importer, _reader).ExecuteAsync(Me, null!, Content, new ImportOptions(false, null, null), Ct));
        await Should.ThrowAsync<ArgumentNullException>(() => new ImportMessage(_importer, _reader).ExecuteAsync(Me, new FolderRef.WellKnown(WellKnownFolder.Inbox), null!, new ImportOptions(false, null, null), Ct));
        await Should.ThrowAsync<ArgumentNullException>(() => new ImportMessage(_importer, _reader).ExecuteAsync(Me, new FolderRef.WellKnown(WellKnownFolder.Inbox), Content, null!, Ct));
        await Should.ThrowAsync<ArgumentNullException>(() => new AddDraftAttachment(_drafts).ExecuteAsync(Me, DraftId, null!, Ct));
        await Should.ThrowAsync<ArgumentNullException>(() => new SendMail(_sender).ExecuteAsync(Me, null!, true, Ct));
    }
}

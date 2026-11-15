using Nightware.Application.Mail;
using Nightware.Application.Mail.Write;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Tests.Mail.Write;

public sealed class ReplyAndForwardUseCaseTests
{
    private static readonly MailboxRef Me = new("me@example.com");
    private static readonly MessageId Id = new("e.orig");
    private static readonly MessageId DraftId = new("d.reply");
    private static readonly Error Boom = Error.NotFound("gone");
    private static readonly ItemBody Body = new(BodyType.Html, "<p>Thanks</p><hr><p>quoted</p>");
    private static readonly ComposedMessage Composed = new() { Body = Body, ToRecipients = [new EmailAddress("sam@example.com")] };

    private static readonly MailMessage Original = new(Id)
    {
        Subject = "Numbers",
        From = new EmailAddress("alex@example.com"),
        ToRecipients = [new EmailAddress("me@example.com")],
        CcRecipients = [new EmailAddress("kim@example.com")],
        InternetMessageId = "orig@example.com",
    };

    private readonly IMailMessageReader _reader = Substitute.For<IMailMessageReader>();
    private readonly IMailSender _sender = Substitute.For<IMailSender>();
    private readonly IDraftStore _drafts = Substitute.For<IDraftStore>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void ArrangeOriginal() =>
        _reader.GetAsync(Me, Id, ReplyComposer.OriginalFetch, Arg.Any<CancellationToken>()).Returns(Original);

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task Reply_composes_from_the_original_and_sends_to_sent_items(bool replyAll, int cc)
    {
        ArrangeOriginal();
        _sender.SendAsync(Me, Arg.Is<DraftContent>(c => c.Subject == "RE: Numbers" && c.Body == Body
            && c.ToRecipients.Single().Address == "alex@example.com" && c.CcRecipients.Count == cc
            && c.InReplyTo == "orig@example.com"), true, Arg.Any<CancellationToken>()).Returns(Result.Success());

        var result = await new ReplyToMessage(_reader, _sender).ExecuteAsync(Me, Id, replyAll, new ComposedMessage { Body = Body }, Ct);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Reply_stops_when_the_original_cannot_be_read()
    {
        _reader.GetAsync(Me, Id, ReplyComposer.OriginalFetch, Arg.Any<CancellationToken>()).Returns(Boom);

        (await new ReplyToMessage(_reader, _sender).ExecuteAsync(Me, Id, false, Composed, Ct)).Error.ShouldBe(Boom);
        await _sender.DidNotReceiveWithAnyArgs().SendAsync(default, default!, default, Ct);
    }

    [Fact]
    public async Task Reply_returns_a_send_failure()
    {
        ArrangeOriginal();
        _sender.SendAsync(Me, Arg.Any<DraftContent>(), true, Arg.Any<CancellationToken>()).Returns(Result.Failure(Boom));

        (await new ReplyToMessage(_reader, _sender).ExecuteAsync(Me, Id, true, Composed, Ct)).Error.ShouldBe(Boom);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task Create_reply_saves_the_reply_as_a_draft(bool replyAll, int cc)
    {
        ArrangeOriginal();
        var draft = new MailMessage(DraftId) { IsDraft = true };
        _drafts.CreateAsync(Me, Arg.Is<DraftContent>(c => c.Subject == "RE: Numbers" && c.CcRecipients.Count == cc && c.InReplyTo == "orig@example.com"), Arg.Any<CancellationToken>())
            .Returns(DraftId);
        _reader.GetAsync(Me, DraftId, MessageFetch.Default, Arg.Any<CancellationToken>()).Returns(draft);

        var result = await new CreateReplyDraft(_reader, _drafts).ExecuteAsync(Me, Id, replyAll, new ComposedMessage { Body = Body }, Ct);

        result.Value.ShouldBe(draft);
        await _sender.DidNotReceiveWithAnyArgs().SendAsync(default, default!, default, Ct);
    }

    [Fact]
    public async Task Create_reply_falls_back_to_the_draft_as_written()
    {
        ArrangeOriginal();
        _drafts.CreateAsync(Me, Arg.Any<DraftContent>(), Arg.Any<CancellationToken>()).Returns(DraftId);
        _reader.GetAsync(Me, DraftId, MessageFetch.Default, Arg.Any<CancellationToken>()).Returns(Boom);

        var written = (await new CreateReplyDraft(_reader, _drafts).ExecuteAsync(Me, Id, false, Composed, Ct)).Value;

        written.Id.ShouldBe(DraftId);
        written.IsDraft.ShouldBeTrue();
        written.Subject.ShouldBe("RE: Numbers");
    }

    [Fact]
    public async Task Create_reply_failures_are_returned()
    {
        _reader.GetAsync(Me, Id, ReplyComposer.OriginalFetch, Arg.Any<CancellationToken>()).Returns(Boom);
        (await new CreateReplyDraft(_reader, _drafts).ExecuteAsync(Me, Id, false, Composed, Ct)).Error.ShouldBe(Boom);
        await _drafts.DidNotReceiveWithAnyArgs().CreateAsync(default, default!, Ct);

        ArrangeOriginal();
        _drafts.CreateAsync(Me, Arg.Any<DraftContent>(), Arg.Any<CancellationToken>()).Returns(Boom);
        (await new CreateReplyDraft(_reader, _drafts).ExecuteAsync(Me, Id, false, Composed, Ct)).Error.ShouldBe(Boom);
    }

    [Fact]
    public async Task Forward_sends_with_the_original_attachments()
    {
        ArrangeOriginal();
        _sender.SendAsync(Me, Arg.Is<DraftContent>(c => c.Subject == "FW: Numbers" && c.Body == Body
            && c.ToRecipients.Single().Address == "sam@example.com" && c.AttachmentsFrom == Id && c.InReplyTo == null), true, Arg.Any<CancellationToken>())
            .Returns(Result.Success());

        (await new ForwardMessage(_reader, _sender).ExecuteAsync(Me, Id, Composed, Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Forward_stops_when_the_original_cannot_be_read()
    {
        _reader.GetAsync(Me, Id, ReplyComposer.OriginalFetch, Arg.Any<CancellationToken>()).Returns(Boom);

        (await new ForwardMessage(_reader, _sender).ExecuteAsync(Me, Id, Composed, Ct)).Error.ShouldBe(Boom);
        await _sender.DidNotReceiveWithAnyArgs().SendAsync(default, default!, default, Ct);
    }

    [Fact]
    public async Task Create_forward_saves_a_draft_carrying_the_attachments()
    {
        ArrangeOriginal();
        var draft = new MailMessage(DraftId) { IsDraft = true, HasAttachments = true };
        _drafts.CreateAsync(Me, Arg.Is<DraftContent>(c => c.Subject == "FW: Numbers" && c.AttachmentsFrom == Id), Arg.Any<CancellationToken>()).Returns(DraftId);
        _reader.GetAsync(Me, DraftId, MessageFetch.Default, Arg.Any<CancellationToken>()).Returns(draft);

        (await new CreateForwardDraft(_reader, _drafts).ExecuteAsync(Me, Id, Composed, Ct)).Value.ShouldBe(draft);
    }

    [Fact]
    public async Task Create_forward_failures_are_returned()
    {
        _reader.GetAsync(Me, Id, ReplyComposer.OriginalFetch, Arg.Any<CancellationToken>()).Returns(Boom);
        (await new CreateForwardDraft(_reader, _drafts).ExecuteAsync(Me, Id, Composed, Ct)).Error.ShouldBe(Boom);

        ArrangeOriginal();
        _drafts.CreateAsync(Me, Arg.Any<DraftContent>(), Arg.Any<CancellationToken>()).Returns(Boom);
        (await new CreateForwardDraft(_reader, _drafts).ExecuteAsync(Me, Id, Composed, Ct)).Error.ShouldBe(Boom);
    }

    [Fact]
    public async Task Reply_and_forward_use_cases_reject_a_null_composition()
    {
        await Should.ThrowAsync<ArgumentNullException>(() => new ReplyToMessage(_reader, _sender).ExecuteAsync(Me, Id, false, null!, Ct));
        await Should.ThrowAsync<ArgumentNullException>(() => new CreateReplyDraft(_reader, _drafts).ExecuteAsync(Me, Id, false, null!, Ct));
        await Should.ThrowAsync<ArgumentNullException>(() => new ForwardMessage(_reader, _sender).ExecuteAsync(Me, Id, null!, Ct));
        await Should.ThrowAsync<ArgumentNullException>(() => new CreateForwardDraft(_reader, _drafts).ExecuteAsync(Me, Id, null!, Ct));
    }
}

using Nightware.Application.Mail;
using Nightware.Application.Mail.Write;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Tests.Mail.Write;

public sealed class MimeUseCaseTests
{
    private static readonly MailboxRef Me = new("me@example.com");
    private static readonly MessageId Draft = new("d.abc");
    private static readonly byte[] Mime = "Subject: x\r\n\r\nhi\r\n"u8.ToArray();

    private readonly IDraftStore _drafts = Substitute.For<IDraftStore>();
    private readonly IMailMessageReader _reader = Substitute.For<IMailMessageReader>();
    private readonly IMailSender _sender = Substitute.For<IMailSender>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_MIME_draft_is_re_read_after_it_is_saved()
    {
        var saved = new MailMessage(Draft) { Subject = "x", IsDraft = true };
        _drafts.CreateFromMimeAsync(Me, Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>()).Returns(Draft);
        _reader.GetAsync(Me, Draft, MessageFetch.Default, Arg.Any<CancellationToken>()).Returns(saved);

        (await new CreateDraftFromMime(_drafts, _reader).ExecuteAsync(Me, Mime, Ct)).Value.ShouldBe(saved);
    }

    [Fact]
    public async Task A_failed_re_read_still_answers_with_the_drafts_id()
    {
        _drafts.CreateFromMimeAsync(Me, Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>()).Returns(Draft);
        _reader.GetAsync(Me, Draft, MessageFetch.Default, Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("down"));

        var message = (await new CreateDraftFromMime(_drafts, _reader).ExecuteAsync(Me, Mime, Ct)).Value;

        (message.Id, message.IsDraft).ShouldBe((Draft, true));
    }

    [Fact]
    public async Task A_store_failure_is_returned()
    {
        _drafts.CreateFromMimeAsync(Me, Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>()).Returns(Error.Validation("bad"));

        (await new CreateDraftFromMime(_drafts, _reader).ExecuteAsync(Me, Mime, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public async Task Empty_or_oversize_MIME_never_reaches_the_ports()
    {
        (await new CreateDraftFromMime(_drafts, _reader).ExecuteAsync(Me, ReadOnlyMemory<byte>.Empty, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await new SendMime(_sender).ExecuteAsync(Me, new byte[CreateDraftFromMime.MaxBytes + 1], Ct)).Error!.Kind.ShouldBe(ErrorKind.PayloadTooLarge);
        _drafts.ReceivedCalls().ShouldBeEmpty();
        _sender.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_MIME_send_passes_through()
    {
        _sender.SendMimeAsync(Me, Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>()).Returns(Result.Success());

        (await new SendMime(_sender).ExecuteAsync(Me, Mime, Ct)).IsSuccess.ShouldBeTrue();
    }
}

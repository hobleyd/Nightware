using Nightware.Application.Mail;
using Nightware.Conformance.Tests.Harness;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Conformance.Tests.Arrangements;

/// <summary>
/// Phase 12 mail parity (ADR 0006 D5), against captures from Microsoft 365. One captured message
/// ("Nightware capture eb09af", sent to the mailbox itself) is flagged, categorised, marked important and copied.
/// </summary>
public sealed class MailParityArrangements : IConformanceArrangements
{
    private const string Me = "me@example.com";
    private const string MessageId = "AAMk-id-0002=";
    private const string CopyId = "AAMk-id-0003=";
    private const string InboxId = "AAMk-id-0001=";
    private const string FolderId = "AAMk-parentFolderId-0001=";
    private const string Category = "Nightware eb09af";

    /// <summary>
    /// Exchange-only members of a full message the façade does not have: change tracking (<c>changeKey</c>),
    /// Outlook links and threading (<c>webLink</c>, <c>conversationIndex</c>), Focused Inbox (cut, ADR 0006 D5)
    /// and receipt requests NightMail never reads.
    /// </summary>
    private static readonly IReadOnlyList<string> ExchangeOnly =
    [
        "changeKey", "conversationIndex", "webLink", "inferenceClassification",
        "isDeliveryReceiptRequested", "isReadReceiptRequested",
    ];

    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        yield return Patch("mail.messages.update-flag",
            p => p.Flag == new FollowUpFlag(FlagStatus.Flagged) && p.Categories == null && p.Importance == null,
            Captured(MessageId, InboxId, "2026-11-20T22:31:46Z") with { Flag = new FollowUpFlag(FlagStatus.Flagged) });

        yield return Patch("mail.messages.update-categories",
            p => p.Categories != null && p.Categories.SequenceEqual(new[] { Category }) && p.Flag == null,
            Captured(MessageId, InboxId, "2026-11-20T22:31:47Z") with { Flag = new FollowUpFlag(FlagStatus.Flagged), Categories = [Category] });

        yield return Patch("mail.messages.update-importance",
            p => p.Importance == Importance.High && p.Categories == null,
            Captured(MessageId, InboxId, "2026-11-20T22:31:47Z") with { Flag = new FollowUpFlag(FlagStatus.Flagged), Categories = [Category], Importance = Importance.High });

        yield return new ConformanceArrangement("mail.messages.copy", static fakes =>
        {
            fakes.For<IMailMessageWriter>()
                .CopyAsync(new MailboxRef(Me), new MessageId(MessageId), new FolderRef.ById(new FolderId(FolderId)), Arg.Any<CancellationToken>())
                .Returns(new MessageId(CopyId));
            fakes.For<IMailMessageReader>()
                .GetAsync(new MailboxRef(Me), new MessageId(CopyId), MessageFetch.Default, Arg.Any<CancellationToken>())
                .Returns(Captured(CopyId, FolderId, "2026-11-20T22:31:47Z") with { Flag = new FollowUpFlag(FlagStatus.Flagged), Categories = [Category], Importance = Importance.High });
        })
        {
            IgnorePaths = ExchangeOnly,
        };
    }

    private static ConformanceArrangement Patch(string operationId, Func<MessagePatch, bool> patch, MailMessage after) =>
        new(operationId, fakes =>
        {
            fakes.For<IMailMessageWriter>()
                .PatchAsync(new MailboxRef(Me), new MessageId(MessageId), Arg.Is<MessagePatch>(p => patch(p)), Arg.Any<CancellationToken>())
                .Returns(Result.Success());
            fakes.For<IMailMessageReader>()
                .GetAsync(new MailboxRef(Me), new MessageId(MessageId), Arg.Any<MessageFetch>(), Arg.Any<CancellationToken>())
                .Returns(after);
        })
        {
            IgnorePaths = ExchangeOnly,
        };

    private static MailMessage Captured(string id, string folderId, string lastModified)
    {
        var me = new EmailAddress(Me, "Me Example");
        return new MailMessage(new MessageId(id))
        {
            Subject = "Nightware capture eb09af",
            BodyPreview = "Nightware capture message eb09af.",
            Body = new ItemBody(BodyType.Text, "Nightware capture message eb09af."),
            From = me,
            Sender = me,
            ToRecipients = [me],
            ReceivedDateTime = DateTimeOffset.Parse("2026-11-20T22:31:44Z", System.Globalization.CultureInfo.InvariantCulture),
            SentDateTime = DateTimeOffset.Parse("2026-11-20T22:31:42Z", System.Globalization.CultureInfo.InvariantCulture),
            CreatedDateTime = DateTimeOffset.Parse("2026-11-20T22:31:44Z", System.Globalization.CultureInfo.InvariantCulture),
            LastModifiedDateTime = DateTimeOffset.Parse(lastModified, System.Globalization.CultureInfo.InvariantCulture),
            ParentFolderId = new FolderId(folderId),
            ConversationId = "AAMk-conversationId-0001=",
            InternetMessageId = "AAMk-internetMessageId-0001=",
        };
    }
}

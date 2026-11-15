using System.Text.Json;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Mail.Ids;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Mail.Write;

/// <summary>
/// A message the façade writes with <c>Email/set create</c> (a draft, its replacement, or an outgoing
/// message). JMAP Emails are immutable apart from keywords and mailboxes, so every content change is a
/// new Email built from one of these; attachments are referenced by <c>blobId</c>, never re-uploaded.
/// </summary>
internal sealed record OutgoingEmail
{
    /// <summary>The partId of the single text body part in the create.</summary>
    public const string BodyPartId = "body";

    /// <summary>Bare Message-ID (no angle brackets); a draft's id is derived from it (ADR 0001 §1).</summary>
    public required string MessageId { get; init; }

    public required IReadOnlyDictionary<string, bool> MailboxIds { get; init; }

    public required IReadOnlyDictionary<string, bool> Keywords { get; init; }

    public EmailAddressValue? From { get; init; }

    public IReadOnlyList<EmailAddressValue> To { get; init; } = [];

    public IReadOnlyList<EmailAddressValue> Cc { get; init; } = [];

    public IReadOnlyList<EmailAddressValue> Bcc { get; init; } = [];

    public IReadOnlyList<EmailAddressValue> ReplyTo { get; init; } = [];

    public string? Subject { get; init; }

    public ItemBody? Body { get; init; }

    public IReadOnlyList<string> InReplyTo { get; init; } = [];

    public IReadOnlyList<string> References { get; init; } = [];

    public Importance Importance { get; init; } = Importance.Normal;

    /// <summary>Further header fields, e.g. a client's <c>x-</c> headers.</summary>
    public IReadOnlyList<InternetHeader> Headers { get; init; } = [];

    /// <summary>
    /// Attachment (and inline) parts, each carrying a <c>blobId</c>: parts read from an existing Email or
    /// made from uploads. <see cref="ToEmail"/> keeps only their settable properties.
    /// </summary>
    public IReadOnlyList<EmailBodyPart> Attachments { get; init; } = [];

    /// <summary>Keywords of a draft the façade saves: unsent and, being the owner's own, already read.</summary>
    public static IReadOnlyDictionary<string, bool> DraftKeywords { get; } = new Dictionary<string, bool>(StringComparer.Ordinal)
    {
        [Core.Protocol.Keywords.Draft] = true,
        [Core.Protocol.Keywords.Seen] = true,
    };

    /// <summary>
    /// The content of <paramref name="content"/>, addressed and stored as given. Threading headers and
    /// header fields are validated here; anything malformed is a 400, never a backend error.
    /// </summary>
    public static Result<OutgoingEmail> FromContent(
        DraftContent content,
        EmailAddress from,
        string messageId,
        IReadOnlyDictionary<string, bool> mailboxIds,
        IReadOnlyDictionary<string, bool> keywords,
        IReadOnlyList<EmailBodyPart> attachments)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(from);

        foreach (var header in content.ExtraHeaders)
        {
            if (!IsHeaderName(header.Name))
            {
                return Error.Validation($"'{header.Name}' is not a valid header name.", "internetMessageHeaders");
            }
        }

        return new OutgoingEmail
        {
            MessageId = messageId,
            MailboxIds = mailboxIds,
            Keywords = keywords,
            From = Address(from),
            To = Addresses(content.ToRecipients),
            Cc = Addresses(content.CcRecipients),
            Bcc = Addresses(content.BccRecipients),
            ReplyTo = Addresses(content.ReplyTo),
            Subject = content.Subject,
            Body = content.Body,
            InReplyTo = MessageIds(content.InReplyTo is null ? [] : [content.InReplyTo]),
            References = MessageIds(content.References),
            Importance = content.Importance,
            Headers = content.ExtraHeaders,
            Attachments = attachments,
        };
    }

    /// <summary>The <c>Email/set</c> create object.</summary>
    public Email ToEmail()
    {
        var html = Body?.ContentType == BodyType.Html;
        IReadOnlyList<EmailBodyPart> bodyPart = [new EmailBodyPart { PartId = BodyPartId, Type = html ? "text/html" : "text/plain" }];

        var extra = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (Importance != Importance.Normal)
        {
            extra[EmailHeaders.Property("Importance", HeaderForm.Text)] = Text(Importance == Importance.High ? "high" : "low");
        }

        foreach (var header in Headers)
        {
            extra[EmailHeaders.Property(header.Name, HeaderForm.Text)] = Text(header.Value);
        }

        return new Email
        {
            MailboxIds = MailboxIds,
            Keywords = Keywords,
            MessageId = [MessageId],
            InReplyTo = NullIfEmpty(InReplyTo),
            References = NullIfEmpty(References),
            From = From is null ? null : [From],
            To = NullIfEmpty(To),
            Cc = NullIfEmpty(Cc),
            Bcc = NullIfEmpty(Bcc),
            ReplyTo = NullIfEmpty(ReplyTo),
            Subject = Subject ?? string.Empty,
            HtmlBody = html ? bodyPart : null,
            TextBody = html ? null : bodyPart,
            BodyValues = new Dictionary<string, EmailBodyValue>(StringComparer.Ordinal) { [BodyPartId] = new(Body?.Content ?? string.Empty) },
            Attachments = Attachments.Count == 0 ? null : [.. Attachments.Select(AttachmentPart)],
            Extra = extra.Count == 0 ? null : extra,
        };
    }

    /// <summary>
    /// A part of an existing Email (or an upload) as it must appear in a create: by <c>blobId</c>, with only
    /// the settable properties (<c>partId</c> and <c>size</c> are server-set).
    /// </summary>
    public static EmailBodyPart AttachmentPart(EmailBodyPart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        return new EmailBodyPart
        {
            BlobId = part.BlobId,
            Type = part.Type,
            Name = part.Name,
            Charset = part.Charset,
            Disposition = part.Disposition ?? "attachment",
            Cid = part.Cid,
            Location = part.Location,
        };
    }

    public static EmailAddressValue Address(EmailAddress address) =>
        new(string.IsNullOrWhiteSpace(address.Name) ? null : address.Name, address.Address.Trim());

    private static IReadOnlyList<EmailAddressValue> Addresses(IReadOnlyList<EmailAddress> addresses) => [.. addresses.Select(Address)];

    /// <summary>Bare, valid msg-ids only: a malformed one would make Stalwart reject the whole create.</summary>
    private static IReadOnlyList<string> MessageIds(IEnumerable<string> ids) =>
        [.. ids.Select(InternetMessageIds.StripBrackets).Where(MailItemId.IsValidMessageId)];

    private static IReadOnlyList<T>? NullIfEmpty<T>(IReadOnlyList<T> list) => list.Count == 0 ? null : list;

    private static JsonElement Text(string value) => JsonSerializer.SerializeToElement(value, JmapJsonContext.Default.String);

    /// <summary>RFC 5322 field-name: printable ASCII except colon.</summary>
    private static bool IsHeaderName(string? name) =>
        !string.IsNullOrEmpty(name) && name.All(static c => c is > ' ' and < (char)127 and not ':');
}

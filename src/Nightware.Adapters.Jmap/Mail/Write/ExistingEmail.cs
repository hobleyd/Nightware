using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Mail.Ids;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Mail.Write;

/// <summary>
/// Reads back what the façade needs from an existing Email to write a new one from it: a draft being
/// replaced (content change, attachment added or removed) or a message being forwarded.
/// </summary>
internal static class ExistingEmail
{
    private const string ImportanceHeader = "Importance";

    /// <summary>Keywords Stalwart derives from the content; a replacement must not inherit stale ones.</summary>
    private static readonly HashSet<string> DerivedKeywords = new(StringComparer.OrdinalIgnoreCase) { "$hasattachment", "$hasnoattachment" };

    /// <summary><c>Email/get</c> properties for <see cref="Snapshot"/>.</summary>
    public static IReadOnlyList<string> Properties { get; } =
    [
        "id", "messageId", "mailboxIds", "keywords", "from", "to", "cc", "bcc", "replyTo", "subject",
        "inReplyTo", "references", EmailHeaders.Property(ImportanceHeader, HeaderForm.Text),
        "textBody", "htmlBody", "attachments", "bodyValues", "bodyStructure",
    ];

    /// <summary><c>Email/get</c> properties for <see cref="Attachments"/> only (a forward's source).</summary>
    public static IReadOnlyList<string> AttachmentProperties { get; } = ["id", "textBody", "htmlBody", "attachments"];

    /// <summary>Per-part properties; <c>subParts</c> so attachment ids can be assigned over <c>bodyStructure</c>.</summary>
    public static IReadOnlyList<string> BodyProperties { get; } = ["partId", "blobId", "size", "name", "type", "charset", "disposition", "cid", "location", "subParts"];

    /// <summary>An <c>Email/get</c> for <paramref name="jmapId"/> that returns everything <see cref="Snapshot"/> reads.</summary>
    public static EmailGetRequest Get(string accountId, string jmapId) => new(accountId)
    {
        Ids = [jmapId],
        Properties = Properties,
        BodyProperties = BodyProperties,
        FetchTextBodyValues = true,
        FetchHtmlBodyValues = true,
    };

    public static bool IsDraft(Email email) =>
        email.Keywords is { } keywords && keywords.TryGetValue(Keywords.Draft, out var draft) && draft;

    /// <summary>
    /// The Message-ID a replacement keeps: the draft's own (so its <c>d.</c> id survives), or a new one
    /// when it has none usable — a draft another client saved without one, still addressed as <c>e.</c>.
    /// </summary>
    public static string? MessageId(Email email) =>
        email.MessageId is [var single] && MailItemId.IsValidMessageId(InternetMessageIds.StripBrackets(single))
            ? InternetMessageIds.StripBrackets(single)
            : null;

    /// <summary>The draft's keywords without the ones the server derives, and always <c>$draft</c>.</summary>
    public static IReadOnlyDictionary<string, bool> DraftKeywords(Email email)
    {
        var keywords = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var (keyword, set) in email.Keywords ?? new Dictionary<string, bool>())
        {
            if (set && !DerivedKeywords.Contains(keyword))
            {
                keywords[keyword] = true;
            }
        }

        keywords[Keywords.Draft] = true;
        return keywords;
    }

    /// <summary>
    /// The Email as a new <see cref="OutgoingEmail"/>, unchanged: same Message-ID, mailboxes, headers,
    /// body (from its body values) and attachments (by blobId).
    /// </summary>
    public static OutgoingEmail Snapshot(Email email, string messageId) => new()
    {
        MessageId = messageId,
        MailboxIds = email.MailboxIds ?? new Dictionary<string, bool>(),
        Keywords = DraftKeywords(email),
        From = email.From is [var from, ..] ? from : null,
        To = email.To ?? [],
        Cc = email.Cc ?? [],
        Bcc = email.Bcc ?? [],
        ReplyTo = email.ReplyTo ?? [],
        Subject = email.Subject,
        Body = Body(email),
        InReplyTo = email.InReplyTo ?? [],
        References = email.References ?? [],
        Importance = Importance(email),
        Attachments = Attachments(email),
    };

    /// <summary>The message text: its first HTML part, else its first plain-text part.</summary>
    public static ItemBody? Body(Email email)
    {
        if (email.HtmlBody?.FirstOrDefault(static p => IsType(p, "text/html")) is { } html && Value(email, html) is { } htmlText)
        {
            return new ItemBody(BodyType.Html, htmlText);
        }

        if (email.TextBody?.FirstOrDefault(static p => IsType(p, "text/plain")) is { } text && Value(email, text) is { } plainText)
        {
            return new ItemBody(BodyType.Text, plainText);
        }

        return null;
    }

    /// <summary>
    /// Every attachment, as create parts that reference the existing blobs. JMAP counts an inline image of
    /// a plain-text message as body, not attachment (RFC 8621 §4.1.4); Graph lists it as an inline
    /// attachment, so non-text body parts are carried too.
    /// </summary>
    public static IReadOnlyList<EmailBodyPart> Attachments(Email email)
    {
        var parts = new List<EmailBodyPart>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in (email.Attachments ?? []).Concat(InlineMedia(email.HtmlBody)).Concat(InlineMedia(email.TextBody)))
        {
            if (part.BlobId is not null && (part.PartId is null || seen.Add(part.PartId)))
            {
                parts.Add(part);
            }
        }

        return parts;
    }

    public static Importance Importance(Email email) =>
        EmailHeaders.GetText(email, ImportanceHeader)?.Trim().ToUpperInvariant() switch
        {
            "HIGH" => Domain.Mail.Importance.High,
            "LOW" => Domain.Mail.Importance.Low,
            _ => Domain.Mail.Importance.Normal,
        };

    private static IEnumerable<EmailBodyPart> InlineMedia(IReadOnlyList<EmailBodyPart>? body) =>
        (body ?? []).Where(static p => p.Type is not null && !p.Type.StartsWith("text/", StringComparison.OrdinalIgnoreCase));

    private static bool IsType(EmailBodyPart part, string type) => string.Equals(part.Type, type, StringComparison.OrdinalIgnoreCase);

    private static string? Value(Email email, EmailBodyPart part) =>
        part.PartId is { } id && email.BodyValues?.TryGetValue(id, out var value) == true ? value.Value : null;
}

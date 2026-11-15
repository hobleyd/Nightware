using System.Globalization;
using System.Net;
using System.Text;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Mail.Ids;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Mail.Read;

/// <summary>The one JMAP <see cref="Email"/> → Domain <see cref="MailMessage"/> mapping (ADR 0001 §35).</summary>
internal static class EmailMapper
{
    private const string Html = "text/html";
    private const string Plain = "text/plain";

    /// <summary>
    /// Maps an Email fetched with <see cref="EmailProperties.List"/> (and, for a single message,
    /// bodies/attachments/headers). <paramref name="folderId"/> is the folder being listed: an Email
    /// in several mailboxes reports that one as <c>parentFolderId</c>.
    /// </summary>
    public static MailMessage ToMessage(Email email, string? folderId = null, BodyType? preferredBody = null)
    {
        ArgumentNullException.ThrowIfNull(email);
        var keywords = email.Keywords ?? new Dictionary<string, bool>();
        var receivedAt = email.ReceivedAt;
        return new MailMessage(MailItemId.For(email).ToMessageId())
        {
            Subject = email.Subject,
            From = First(email.From),
            Sender = First(email.Sender),
            ToRecipients = Addresses(email.To),
            CcRecipients = Addresses(email.Cc),
            BccRecipients = Addresses(email.Bcc),
            ReplyTo = Addresses(email.ReplyTo),
            BodyPreview = email.Preview,
            Body = email.BodyValues is null ? null : Body(email, preferredBody),
            IsRead = Has(keywords, Keywords.Seen),
            IsDraft = Has(keywords, Keywords.Draft),
            Flag = Has(keywords, Keywords.Flagged) ? new FollowUpFlag(FlagStatus.Flagged) : FollowUpFlag.NotFlagged,
            ReceivedDateTime = receivedAt,
            SentDateTime = email.SentAt ?? receivedAt,
            CreatedDateTime = receivedAt,
            LastModifiedDateTime = receivedAt,
            Importance = ImportanceOf(email),
            ConversationId = email.ThreadId,
            HasAttachments = email.HasAttachment ?? false,
            ParentFolderId = ParentFolderOf(email.MailboxIds, folderId) is { } parent ? new FolderId(parent) : null,
            InternetMessageId = email.MessageId is [var messageId, ..] ? InternetMessageIds.StripBrackets(messageId) : null,
            Categories = [.. keywords.Where(static k => k.Value && !k.Key.StartsWith('$')).Select(static k => k.Key).Order(StringComparer.Ordinal)],
            InternetMessageHeaders = Headers(email.Headers),
            Attachments = email.Attachments is null ? [] : MimeParts.Attachments(email),
            CalendarPart = MimeParts.CalendarPartOf(email.BodyStructure),
        };
    }

    /// <summary>
    /// Graph has one parent folder; JMAP may file an Email in several mailboxes. The listed folder
    /// wins; otherwise the ordinally smallest id, so the answer is stable across requests.
    /// </summary>
    public static string? ParentFolderOf(IReadOnlyDictionary<string, bool>? mailboxIds, string? folderId)
    {
        if (mailboxIds is null)
        {
            return null;
        }

        var ids = mailboxIds.Where(static m => m.Value).Select(static m => m.Key).ToList();
        if (folderId is not null && ids.Contains(folderId, StringComparer.Ordinal))
        {
            return folderId;
        }

        return ids.Order(StringComparer.Ordinal).FirstOrDefault();
    }

    /// <summary>True when the Email is filed in <paramref name="mailboxId"/>.</summary>
    public static bool IsIn(Email email, string mailboxId) =>
        email.MailboxIds is { } ids && ids.TryGetValue(mailboxId, out var member) && member;

    /// <summary>
    /// The body Graph would return. HTML when the message has an HTML part (the default), its
    /// <c>text/plain</c> parts when it has none, and — with <c>Prefer: outlook.body-content-type</c> —
    /// converted to the asked-for type. The type comes from the parts, not from which list they are
    /// in: RFC 8621 falls back to the text parts in <c>htmlBody</c> when there is no HTML.
    /// </summary>
    internal static ItemBody Body(Email email, BodyType? preferred)
    {
        var values = email.BodyValues!;
        var html = Parts(email.HtmlBody, values, Html, Plain);
        var hasHtml = html.Any(static p => p.Type == Html);
        var text = Parts(email.TextBody, values, Plain);

        if (preferred == BodyType.Text)
        {
            return text.Count > 0 || !hasHtml
                ? new ItemBody(BodyType.Text, Join(text))
                : new ItemBody(BodyType.Text, HtmlToText.Convert(AsHtml(html)));
        }

        if (hasHtml)
        {
            return new ItemBody(BodyType.Html, AsHtml(html));
        }

        return preferred == BodyType.Html
            ? new ItemBody(BodyType.Html, TextAsHtml(Join(text)))
            : new ItemBody(BodyType.Text, Join(text));
    }

    private static List<(string Type, string Value)> Parts(IReadOnlyList<EmailBodyPart>? parts, IReadOnlyDictionary<string, EmailBodyValue> values, params string[] types)
    {
        var result = new List<(string, string)>();
        foreach (var part in parts ?? [])
        {
            var type = part.Type?.ToLowerInvariant();
            if (part.PartId is { } id && type is not null && types.Contains(type) && values.TryGetValue(id, out var value))
            {
                result.Add((type, value.Value));
            }
        }

        return result;
    }

    private static string Join(List<(string Type, string Value)> parts) => string.Concat(parts.Select(static p => p.Value));

    private static string AsHtml(List<(string Type, string Value)> parts) =>
        string.Concat(parts.Select(static p => p.Type == Html ? p.Value : TextAsHtml(p.Value)));

    private static string TextAsHtml(string text) =>
        "<div>" + WebUtility.HtmlEncode(text).Replace("\r\n", "<br>", StringComparison.Ordinal).Replace("\n", "<br>", StringComparison.Ordinal) + "</div>";

    /// <summary>Header values as Graph shows them: unfolded, without the space after the colon.</summary>
    private static IReadOnlyList<InternetHeader> Headers(IReadOnlyList<EmailHeaderValue>? headers) =>
        headers is null ? [] : [.. headers.Select(static h => new InternetHeader(h.Name, Unfold(h.Value)))];

    private static string Unfold(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\r' && i + 1 < value.Length && value[i + 1] == '\n')
            {
                i++;
                continue;
            }

            if (value[i] != '\n')
            {
                builder.Append(value[i]);
            }
        }

        return builder.ToString().Trim();
    }

    /// <summary><c>Importance:</c> first, then the de-facto <c>X-Priority:</c> (1–2 high, 4–5 low).</summary>
    private static Importance ImportanceOf(Email email)
    {
        var importance = EmailHeaders.GetText(email, "Importance")?.Trim();
        if (string.Equals(importance, "high", StringComparison.OrdinalIgnoreCase))
        {
            return Importance.High;
        }

        if (string.Equals(importance, "low", StringComparison.OrdinalIgnoreCase))
        {
            return Importance.Low;
        }

        var priority = EmailHeaders.GetText(email, "X-Priority")?.Trim();
        if (priority is { Length: > 0 } && int.TryParse(priority.AsSpan(0, 1), NumberStyles.None, CultureInfo.InvariantCulture, out var level))
        {
            return level switch
            {
                1 or 2 => Importance.High,
                4 or 5 => Importance.Low,
                _ => Importance.Normal,
            };
        }

        return Importance.Normal;
    }

    private static bool Has(IReadOnlyDictionary<string, bool> keywords, string keyword) =>
        keywords.TryGetValue(keyword, out var set) && set;

    private static EmailAddress? First(IReadOnlyList<EmailAddressValue>? addresses) =>
        addresses is [var first, ..] ? ToAddress(first) : null;

    private static IReadOnlyList<EmailAddress> Addresses(IReadOnlyList<EmailAddressValue>? addresses) =>
        addresses is null ? [] : [.. addresses.Where(static a => !string.IsNullOrEmpty(a.Email)).Select(ToAddress)];

    private static EmailAddress ToAddress(EmailAddressValue value) =>
        new(value.Email, string.IsNullOrWhiteSpace(value.Name) ? null : value.Name);
}

using System.Buffers.Text;
using System.Text;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Mail.Ids;

internal enum MailItemKind
{
    /// <summary><c>e.&lt;jmapId&gt;</c>: an ordinary message, addressed by its (move-stable) JMAP id.</summary>
    Email,

    /// <summary><c>d.&lt;base64url(Message-ID)&gt;</c>: a draft, addressed by its Message-ID, which survives replacement.</summary>
    Draft,
}

/// <summary>
/// FROZEN wire format of Graph message ids (ADR 0001 §1). Changing it breaks every id
/// a client has stored. Canonical forms only: every id has exactly one spelling.
/// <list type="bullet">
/// <item><c>e.</c> + JMAP Id (1–255 chars of <c>[A-Za-z0-9_-]</c>, RFC 8620 §1.2).</item>
/// <item><c>d.</c> + unpadded base64url of the UTF-8 Message-ID without angle brackets.</item>
/// </list>
/// </summary>
internal readonly record struct MailItemId
{
    public const string EmailPrefix = "e.";
    public const string DraftPrefix = "d.";

    /// <summary>RFC 5322 line limit; a longer Message-ID cannot be a real header.</summary>
    private const int MaxMessageIdLength = 998;

    private MailItemId(MailItemKind kind, string value)
    {
        Kind = kind;
        Value = value;
    }

    public MailItemKind Kind { get; }

    /// <summary>The JMAP id (<see cref="MailItemKind.Email"/>) or the bare Message-ID (<see cref="MailItemKind.Draft"/>).</summary>
    public string Value { get; }

    public bool IsDraft => Kind == MailItemKind.Draft;

    public static MailItemId ForEmail(string jmapId)
    {
        if (!IsValidJmapId(jmapId))
        {
            throw new ArgumentException("Not a valid JMAP id.", nameof(jmapId));
        }

        return new(MailItemKind.Email, jmapId);
    }

    /// <param name="messageId">The Message-ID with or without angle brackets.</param>
    public static MailItemId ForDraft(string messageId)
    {
        var bare = InternetMessageIds.StripBrackets(messageId);
        if (!IsValidMessageId(bare))
        {
            throw new ArgumentException("Not a valid Message-ID.", nameof(messageId));
        }

        return new(MailItemKind.Draft, bare);
    }

    /// <summary>
    /// The id to emit for an Email. Drafts (<c>$draft</c> keyword) with exactly one valid
    /// Message-ID get the <c>d.</c> form; everything else — including a draft without a
    /// usable Message-ID — gets <c>e.</c>. Fetch <c>keywords</c> and <c>messageId</c> on every Email/get.
    /// </summary>
    public static MailItemId For(string jmapId, IReadOnlyDictionary<string, bool>? keywords, IReadOnlyList<string>? messageIds)
    {
        if (keywords is not null
            && keywords.TryGetValue(Keywords.Draft, out var isDraft) && isDraft
            && messageIds is [var single]
            && IsValidMessageId(InternetMessageIds.StripBrackets(single)))
        {
            return ForDraft(single);
        }

        return ForEmail(jmapId);
    }

    /// <inheritdoc cref="For(string, IReadOnlyDictionary{string, bool}?, IReadOnlyList{string}?)"/>
    public static MailItemId For(Email email)
    {
        ArgumentNullException.ThrowIfNull(email);
        return For(email.Id ?? throw new ArgumentException("Email has no id.", nameof(email)), email.Keywords, email.MessageId);
    }

    /// <summary>Parses a Graph message id. Anything not in canonical form is a malformed id (HTTP 400).</summary>
    public static Result<MailItemId> Parse(MessageId id)
    {
        var text = id.Value;
        if (text is null)
        {
            return Malformed();
        }

        if (text.StartsWith(EmailPrefix, StringComparison.Ordinal))
        {
            var jmapId = text[EmailPrefix.Length..];
            return IsValidJmapId(jmapId) ? new MailItemId(MailItemKind.Email, jmapId) : Malformed();
        }

        if (text.StartsWith(DraftPrefix, StringComparison.Ordinal))
        {
            var encoded = text[DraftPrefix.Length..];
            if (encoded.Length == 0 || !Base64Url.IsValid(encoded) || encoded.Contains('=', StringComparison.Ordinal))
            {
                return Malformed();
            }

            string messageId;
            try
            {
                messageId = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(Base64Url.DecodeFromChars(encoded));
            }
            catch (FormatException)
            {
                return Malformed();
            }
            catch (ArgumentException)
            {
                return Malformed();
            }

            // Canonical check: the decoded value must re-encode to exactly the input
            // (rejects non-zero trailing bits, i.e. two spellings of one draft).
            if (!IsValidMessageId(messageId) || !string.Equals(Encode(messageId), encoded, StringComparison.Ordinal))
            {
                return Malformed();
            }

            return new MailItemId(MailItemKind.Draft, messageId);
        }

        return Malformed();
    }

    public static bool IsValidJmapId(string? value) =>
        value is { Length: >= 1 and <= 255 } && value.All(c => c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_');

    /// <summary>A bare Message-ID: non-empty, printable ASCII, no whitespace or angle brackets.</summary>
    public static bool IsValidMessageId(string? value) =>
        value is { Length: >= 1 and <= MaxMessageIdLength } && value.All(c => c is > ' ' and < (char)127 and not '<' and not '>');

    /// <summary>
    /// The <c>Email/query</c> filter that finds the draft behind a <c>d.</c> id. RFC 8621 header
    /// matching is "contains", so always confirm candidates with <see cref="MatchesDraft"/>.
    /// </summary>
    public EmailFilter DraftFilter(string draftsMailboxId)
    {
        if (!IsDraft)
        {
            throw new InvalidOperationException("Only draft ids resolve through a Message-ID filter.");
        }

        ArgumentException.ThrowIfNullOrEmpty(draftsMailboxId);
        return new EmailFilter
        {
            Operator = "AND",
            Conditions =
            [
                new EmailFilter { InMailbox = draftsMailboxId },
                new EmailFilter { Header = ["Message-ID", Value] },
            ],
        };
    }

    /// <summary>True when <paramref name="email"/> (fetched with <c>messageId</c>) is exactly this draft.</summary>
    public bool MatchesDraft(Email email)
    {
        ArgumentNullException.ThrowIfNull(email);
        var value = Value;
        return IsDraft && email.MessageId is [var single] && string.Equals(InternetMessageIds.StripBrackets(single), value, StringComparison.Ordinal);
    }

    public MessageId ToMessageId() => new(ToString());

    public override string ToString() => Kind == MailItemKind.Email ? EmailPrefix + Value : DraftPrefix + Encode(Value);

    private static string Encode(string messageId) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(messageId));

    private static Error Malformed() =>
        new(ErrorKind.Validation, "ErrorInvalidIdMalformed", "The message id is malformed.", "id");
}

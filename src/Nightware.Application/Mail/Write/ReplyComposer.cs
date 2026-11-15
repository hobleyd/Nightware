using System.Text.RegularExpressions;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Mail.Write;

public enum ReplyKind
{
    Reply,
    ReplyAll,
    Forward,
}

/// <summary>
/// What the client composed for a reply or forward (Graph <c>message</c> in the action body). A null
/// or empty member was not sent, so Graph's default applies; a list that was sent replaces the default.
/// </summary>
public sealed record ComposedMessage
{
    public string? Subject { get; init; }

    public ItemBody? Body { get; init; }

    public IReadOnlyList<EmailAddress>? ToRecipients { get; init; }

    public IReadOnlyList<EmailAddress>? CcRecipients { get; init; }

    public IReadOnlyList<EmailAddress>? BccRecipients { get; init; }
}

/// <summary>
/// Turns an original message and what the client composed into the outgoing <see cref="DraftContent"/>
/// of a reply, reply-all or forward, the way Outlook does (ADR 0001 §11).
/// </summary>
/// <remarks>
/// NightMail sends the whole body it composed, the quoted original included, and no <c>comment</c>
/// (endpoints.md, "Where the phase plans and the client disagree"). So the body is used verbatim and
/// never quoted again; the composer only adds what the client cannot: threading headers, the
/// <c>RE:</c>/<c>FW:</c> prefix when no subject was given, default recipients, and (for a forward)
/// the original's attachments.
/// </remarks>
public static partial class ReplyComposer
{
    /// <summary>RFC 5322 §3.6.4 lets <c>References</c> be trimmed; keep the root and the most recent ancestors.</summary>
    public const int MaxReferences = 20;

    /// <summary>What <see cref="Compose"/> reads from the original; fetch it with this.</summary>
    public static MessageFetch OriginalFetch { get; } = new(new HashSet<string>(StringComparer.Ordinal)
    {
        "subject",
        "from",
        "sender",
        "replyTo",
        "toRecipients",
        "ccRecipients",
        "internetMessageId",
        "internetMessageHeaders",
    });

    public static DraftContent Compose(MailMessage original, ReplyKind kind, ComposedMessage composed, MailboxRef self)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(composed);

        var (to, cc) = kind switch
        {
            ReplyKind.Reply => (Given(composed.ToRecipients) ?? ReplyTargets(original, self), Given(composed.CcRecipients) ?? []),
            ReplyKind.ReplyAll => ReplyAllRecipients(original, composed, self),
            ReplyKind.Forward => (Given(composed.ToRecipients) ?? [], Given(composed.CcRecipients) ?? []),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var originalId = original.InternetMessageId is { Length: > 0 } id ? Bare(id) : null;
        return new DraftContent
        {
            Subject = Subject(original.Subject, composed.Subject, kind),
            Body = composed.Body ?? new ItemBody(BodyType.Html, string.Empty),
            ToRecipients = to,
            CcRecipients = cc,
            BccRecipients = Given(composed.BccRecipients) ?? [],

            // A forward is not an answer to the original, but it stays in its conversation: References
            // threads it (as Outlook keeps it in the same conversationId) without claiming In-Reply-To.
            InReplyTo = kind == ReplyKind.Forward ? null : originalId,
            References = References(original, originalId),
            AttachmentsFrom = kind == ReplyKind.Forward ? original.Id : null,
        };
    }

    /// <summary>The composed subject verbatim, else the original's with the prefix added unless it is already there.</summary>
    public static string Subject(string? original, string? composed, ReplyKind kind)
    {
        if (!string.IsNullOrWhiteSpace(composed))
        {
            return composed;
        }

        var subject = original?.Trim() ?? string.Empty;
        var prefixed = kind == ReplyKind.Forward ? ForwardPrefix().IsMatch(subject) : ReplyPrefix().IsMatch(subject);
        if (prefixed)
        {
            return subject;
        }

        return (kind == ReplyKind.Forward ? "FW: " : "RE: ") + subject;
    }

    private static (IReadOnlyList<EmailAddress> To, IReadOnlyList<EmailAddress> Cc) ReplyAllRecipients(MailMessage original, ComposedMessage composed, MailboxRef self)
    {
        var givenTo = Given(composed.ToRecipients);
        var givenCc = Given(composed.CcRecipients);
        if (givenTo is not null && givenCc is not null)
        {
            return (givenTo, givenCc);
        }

        // The sender (or Reply-To) plus every original To, then the original Cc; the mailbox itself is
        // never a recipient. Replying to your own sent message keeps its recipients as they were.
        IReadOnlyList<EmailAddress> to = givenTo ?? Distinct([.. ReplyTargets(original, self), .. original.ToRecipients], exclude: [], self);
        if (to.Count == 0)
        {
            to = ReplyTargets(original, self);
        }

        var cc = givenCc ?? Distinct(original.CcRecipients, exclude: to, self);
        return (to, cc);
    }

    /// <summary>Who a plain reply goes to: Reply-To, else From; for a message the mailbox sent itself, its To.</summary>
    private static IReadOnlyList<EmailAddress> ReplyTargets(MailMessage original, MailboxRef self)
    {
        var from = original.From ?? original.Sender;
        if (from is not null && IsSelf(from, self) && original.ToRecipients.Count > 0)
        {
            return original.ToRecipients;
        }

        if (original.ReplyTo.Count > 0)
        {
            return original.ReplyTo;
        }

        return from is null ? [] : [from];
    }

    private static List<EmailAddress> Distinct(IEnumerable<EmailAddress> addresses, IReadOnlyList<EmailAddress> exclude, MailboxRef self)
    {
        var seen = new HashSet<string>(exclude.Select(static a => Normalise(a.Address)), StringComparer.OrdinalIgnoreCase);
        var result = new List<EmailAddress>();
        foreach (var address in addresses)
        {
            if (!IsSelf(address, self) && seen.Add(Normalise(address.Address)))
            {
                result.Add(address);
            }
        }

        return result;
    }

    /// <summary>The original's References (or its In-Reply-To when it has none) plus the original itself, trimmed.</summary>
    private static List<string> References(MailMessage original, string? originalId)
    {
        var ancestors = MessageIds(Header(original, "References"));
        if (ancestors.Count == 0)
        {
            ancestors = MessageIds(Header(original, "In-Reply-To"));
            if (ancestors.Count > 1)
            {
                ancestors = [];
            }
        }

        var chain = new List<string>();
        foreach (var id in originalId is null ? ancestors : [.. ancestors, originalId])
        {
            if (!chain.Contains(id, StringComparer.Ordinal))
            {
                chain.Add(id);
            }
        }

        return chain.Count <= MaxReferences ? chain : [chain[0], .. chain[^(MaxReferences - 1)..]];
    }

    private static string? Header(MailMessage message, string name) =>
        message.InternetMessageHeaders.LastOrDefault(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;

    /// <summary>The <c>msg-id</c>s of a header, without angle brackets.</summary>
    public static IReadOnlyList<string> MessageIds(string? header) =>
        header is null ? [] : [.. MsgId().Matches(header).Select(static m => m.Groups[1].Value)];

    private static IReadOnlyList<EmailAddress>? Given(IReadOnlyList<EmailAddress>? list) => list is { Count: > 0 } ? list : null;

    private static bool IsSelf(EmailAddress address, MailboxRef self) =>
        string.Equals(Normalise(address.Address), Normalise(self.Address), StringComparison.OrdinalIgnoreCase);

    private static string Normalise(string? address) => address?.Trim() ?? string.Empty;

    private static string Bare(string messageId)
    {
        var trimmed = messageId.Trim();
        return trimmed.Length >= 2 && trimmed[0] == '<' && trimmed[^1] == '>' ? trimmed[1..^1] : trimmed;
    }

    [GeneratedRegex(@"^\s*re\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReplyPrefix();

    [GeneratedRegex(@"^\s*(fw|fwd)\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ForwardPrefix();

    [GeneratedRegex(@"<([^<>\s]+)>", RegexOptions.CultureInvariant)]
    private static partial Regex MsgId();
}

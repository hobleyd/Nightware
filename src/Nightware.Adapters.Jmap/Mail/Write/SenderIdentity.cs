using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Mail.Write;

/// <summary>Who a message the façade writes is from, and which JMAP Identity may send it.</summary>
internal static class SenderIdentity
{
    /// <summary>
    /// The mailbox's own address, named after the caller when it is the caller's mailbox (a shared
    /// mailbox has no name the façade knows).
    /// </summary>
    public static EmailAddress DefaultFrom(MailboxRef mailbox, IUserContext user)
    {
        var principal = user.Principal;
        var name = principal is not null && SameAddress(principal.Mail, mailbox.Address) && !string.IsNullOrWhiteSpace(principal.DisplayName)
            ? principal.DisplayName
            : null;
        return new EmailAddress(mailbox.Address, name);
    }

    /// <summary>
    /// The identity that sends as <paramref name="from"/>: an exact address match, else a wildcard
    /// (<c>*@domain</c>, RFC 8621 §6). Null when none may.
    /// </summary>
    public static Identity? Find(IReadOnlyList<Identity> identities, string from)
    {
        var exact = identities.Where(i => SameAddress(i.Email, from)).ToList();
        if (exact.Count > 0)
        {
            return exact[0];
        }

        var at = from.LastIndexOf('@');
        return at < 0
            ? null
            : identities.FirstOrDefault(i => i.Email is { } email && email.StartsWith("*@", StringComparison.Ordinal) && SameAddress(email[1..], from[at..]));
    }

    /// <summary>
    /// Stalwart creates no Identity for an account, and sending needs one. The façade creates it, but
    /// only for the mailbox's own address: any other From is a send-as the backend has not granted.
    /// </summary>
    public static bool MayCreateFor(MailboxRef mailbox, string from) => SameAddress(mailbox.Address, from);

    /// <summary>Graph's answer to a From the mailbox may not send as (403, so NightMail's migration retries without it).</summary>
    public static Error SendAsDenied(string from) =>
        new(ErrorKind.Forbidden, "ErrorSendAsDenied", $"The mailbox is not allowed to send as '{from}'.");

    public static bool SameAddress(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}

using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Core.Session;

/// <summary>A JMAP account resolved for a mailbox, with the session it belongs to.</summary>
internal sealed record JmapAccount(string AccountId, string Name, bool IsPrimary, bool IsReadOnly, JmapSession Session)
{
    /// <summary>Per-object JSON (<c>urn:ietf:params:jmap:metadata</c>), where Graph-only message state lives (ADR 0006 D5).</summary>
    public bool SupportsMetadata => Session.HasAccountCapability(AccountId, JmapCapabilities.Metadata);
}

/// <summary>
/// Maps a <see cref="MailboxRef"/> (SMTP address) to a JMAP account in a session.
/// <list type="bullet">
/// <item>The caller's own address (session <c>username</c>, the principal's mail, or the primary
/// account's name) → the primary account for the capability.</item>
/// <item>Any other address → the one account in <c>session.accounts</c> whose name matches. Names
/// without <c>@</c> are qualified with the domain of the session username.</item>
/// <item>No match → <see cref="ErrorKind.NotFound"/>; a match that lacks the capability →
/// <see cref="ErrorKind.Forbidden"/> (ADR 0001 §19: the shared-mailbox probe gets 404/403).</item>
/// </list>
/// </summary>
internal static class JmapAccountResolver
{
    public static Result<JmapAccount> Resolve(JmapSession session, MailboxRef mailbox, string? callerAddress, string capability = JmapCapabilities.Mail)
    {
        ArgumentNullException.ThrowIfNull(session);
        var address = (mailbox.Address ?? string.Empty).Trim();
        if (address.Length == 0)
        {
            return Error.NotFound("No mailbox address given.");
        }

        var primaryId = session.PrimaryAccountId(capability);
        var primary = primaryId is not null && session.Accounts.TryGetValue(primaryId, out var p) ? p : null;
        var isOwn = Same(address, session.Username)
            || Same(address, callerAddress)
            || (primary is not null && Same(address, Qualify(primary.Name, session.Username)));
        if (isOwn)
        {
            return primary is null
                ? Error.NotFound($"The mail server has no {capability} account for the signed-in user.")
                : new JmapAccount(primaryId!, primary.Name, true, primary.IsReadOnly, session);
        }

        var matches = session.Accounts
            .Where(a => Same(address, Qualify(a.Value.Name, session.Username)))
            .ToList();
        if (matches.Count == 0)
        {
            return Error.NotFound($"Mailbox '{address}' was not found or is not shared with the signed-in user.");
        }

        var usable = matches.Where(a => a.Value.AccountCapabilities.ContainsKey(capability)).ToList();
        if (usable.Count == 0)
        {
            return Error.Forbidden($"The signed-in user cannot access mail in '{address}'.");
        }

        if (usable.Count > 1)
        {
            return Error.Conflict($"More than one mail account is named '{address}'.");
        }

        var (id, account) = usable[0];
        return new JmapAccount(id, account.Name, id == primaryId, account.IsReadOnly, session);
    }

    /// <summary>Qualifies a bare account name with the domain of <paramref name="username"/>.</summary>
    internal static string Qualify(string name, string? username)
    {
        if (string.IsNullOrEmpty(name) || name.Contains('@', StringComparison.Ordinal))
        {
            return name;
        }

        var at = username?.LastIndexOf('@') ?? -1;
        return at > 0 ? $"{name}@{username![(at + 1)..]}" : name;
    }

    private static bool Same(string address, string? other) =>
        !string.IsNullOrEmpty(other) && string.Equals(address, other.Trim(), StringComparison.OrdinalIgnoreCase);
}

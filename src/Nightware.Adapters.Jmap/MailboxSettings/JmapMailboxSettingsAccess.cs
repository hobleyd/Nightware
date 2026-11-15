using Nightware.Adapters.Jmap.Core;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Application.MailboxSettings;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.MailboxSettings;

/// <summary>
/// <see cref="IMailboxSettingsAccess"/> from the caller's JMAP session: the mailbox must be the
/// caller's own or an account shared with them that carries the <c>VacationResponse</c> capability,
/// the same test the automatic-reply store applies (no account → 404, no capability → 403).
/// </summary>
internal sealed class JmapMailboxSettingsAccess(IJmapClient jmap) : IMailboxSettingsAccess
{
    public async Task<Result> CheckAsync(MailboxRef mailbox, CancellationToken cancellationToken)
    {
        var account = await jmap.ResolveAccountAsync(mailbox, cancellationToken, JmapCapabilities.VacationResponse).ConfigureAwait(false);
        return account.ToResult();
    }
}

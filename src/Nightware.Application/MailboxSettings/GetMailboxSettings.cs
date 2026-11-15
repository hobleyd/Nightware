using Nightware.Application.Directory;
using Nightware.Domain.Kernel;
using Nightware.Domain.MailboxSettings;

namespace Nightware.Application.MailboxSettings;

/// <summary><c>GET mailboxSettings</c>: automatic replies (mail store) and time zone (directory).</summary>
/// <remarks>
/// The directory is read with the façade's service account, which can see every user, so it must
/// not decide who may open a mailbox. The mail store is asked first with the caller's own
/// credentials; only when it grants access is the directory consulted.
/// </remarks>
public sealed class GetMailboxSettings(IAutoReplyStore replies, IUserPreferencesStore preferences)
{
    public async Task<Result<MailboxSettingsSnapshot>> ExecuteAsync(MailboxRef mailbox, CancellationToken cancellationToken)
    {
        var current = await replies.GetAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!current.IsSuccess)
        {
            return current.Error;
        }

        var stored = MailboxSettingsComposer.OrNone(await preferences.GetAsync(mailbox, cancellationToken).ConfigureAwait(false));
        return stored.Map(p => MailboxSettingsComposer.Compose(current.Value, p));
    }
}

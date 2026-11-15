using Nightware.Domain.Kernel;

namespace Nightware.Application.MailboxSettings;

/// <summary>
/// Whether the caller may open a mailbox's settings, decided by the mail store acting as the caller.
/// </summary>
/// <remarks>
/// The time zone and other preferences are read and written with the façade's service account, which
/// can see every user, so they cannot be what refuses a stranger. The mailbox-settings use cases ask
/// this first and touch no store when it fails: authorisation must not depend on which store happens
/// to be called first.
/// </remarks>
public interface IMailboxSettingsAccess
{
    /// <summary>Success when the caller may open <paramref name="mailbox"/>'s settings; NotFound or Forbidden otherwise.</summary>
    Task<Result> CheckAsync(MailboxRef mailbox, CancellationToken cancellationToken);
}

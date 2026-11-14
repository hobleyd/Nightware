using Nightware.Domain.Kernel;
using Nightware.Domain.MailboxSettings;

namespace Nightware.Application.MailboxSettings;

/// <summary>Automatic replies (out of office), backed by JMAP <c>VacationResponse</c>.</summary>
public interface IAutoReplyStore
{
    Task<Result<AutoReplySetting>> GetAsync(MailboxRef mailbox, CancellationToken cancellationToken);

    Task<Result<AutoReplySetting>> SetAsync(MailboxRef mailbox, AutoReplySetting setting, CancellationToken cancellationToken);
}

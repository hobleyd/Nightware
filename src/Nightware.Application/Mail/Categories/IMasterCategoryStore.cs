using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Mail.Categories;

/// <summary>Where a mailbox's master category list is kept. The use cases own the rules; the store keeps the list.</summary>
public interface IMasterCategoryStore
{
    /// <summary>The stored list, or null when the mailbox has never had one written (it then has Outlook's defaults).</summary>
    Task<Result<IReadOnlyList<MasterCategory>?>> ListAsync(MailboxRef mailbox, CancellationToken cancellationToken);

    /// <summary>Replaces the list.</summary>
    Task<Result> SaveAsync(MailboxRef mailbox, IReadOnlyList<MasterCategory> categories, CancellationToken cancellationToken);
}

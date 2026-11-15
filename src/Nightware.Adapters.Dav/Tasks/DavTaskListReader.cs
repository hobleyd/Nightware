using Nightware.Adapters.Dav.Core;
using Nightware.Application.Common.Query;
using Nightware.Application.Tasks;
using Nightware.Domain.Kernel;
using Nightware.Domain.Tasks;

namespace Nightware.Adapters.Dav.Tasks;

/// <summary><see cref="ITaskListReader"/> over the mailbox's VTODO calendars (see <see cref="DavTaskCollections"/>).</summary>
internal sealed class DavTaskListReader(DavTaskCollections collections) : ITaskListReader
{
    public const string DefaultListName = "defaultList";
    public const string NoWellKnownName = "none";

    public async Task<Result<Page<TaskList>>> ListAsync(MailboxRef mailbox, ItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Filter is not null || query.OrderBy.Count > 0)
        {
            return Error.Validation("$filter and $orderby are not supported on todo/lists.");
        }

        var lists = await collections.ListAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!lists.IsSuccess)
        {
            return lists.Error;
        }

        var all = lists.Value
            .OrderBy(l => l.IsDefault ? 0 : 1)
            .ThenBy(l => l.Calendar.Order ?? int.MaxValue)
            .ThenBy(l => l.Calendar.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(l => new TaskList(DavItemId.Resource(l.Href).ToTaskListId(), l.Calendar.DisplayName)
            {
                IsOwner = !l.Calendar.IsShared,
                IsShared = l.Calendar.IsShared,
                WellKnownListName = l.IsDefault ? DefaultListName : NoWellKnownName,
            })
            .ToList();
        return TaskPaging.Page(all, query);
    }
}

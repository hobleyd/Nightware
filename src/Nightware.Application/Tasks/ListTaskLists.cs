using Nightware.Application.Common.Query;
using Nightware.Domain.Kernel;
using Nightware.Domain.Tasks;

namespace Nightware.Application.Tasks;

/// <summary>The mailbox's To Do lists (<c>GET todo/lists</c>).</summary>
public sealed class ListTaskLists(ITaskListReader lists)
{
    public Task<Result<Page<TaskList>>> ExecuteAsync(MailboxRef mailbox, ItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return lists.ListAsync(mailbox, query, cancellationToken);
    }
}

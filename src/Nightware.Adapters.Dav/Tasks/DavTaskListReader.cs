using Nightware.Adapters.Dav.Core;
using Nightware.Application.Common.Query;
using Nightware.Application.Tasks;
using Nightware.Domain.Kernel;
using Nightware.Domain.Tasks;

namespace Nightware.Adapters.Dav.Tasks;

/// <summary>
/// <see cref="ITaskListReader"/> and <see cref="ITaskListStore"/> over the mailbox's VTODO calendars (see
/// <see cref="DavTaskCollections"/>). A new list is a VTODO-only calendar, <c>nightware-{guid}</c>. A collection
/// that also holds events is never deleted: that would take the user's calendar with it.
/// </summary>
internal sealed class DavTaskListReader(DavTaskCollections collections, IDavClient dav, DavPaths paths) : ITaskListReader, ITaskListStore
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
            .Select(ToList)
            .ToList();
        return TaskPaging.Page(all, query);
    }

    public async Task<Result<TaskList>> GetAsync(MailboxRef mailbox, TaskListId list, CancellationToken cancellationToken)
    {
        var found = await FindAsync(mailbox, list, cancellationToken).ConfigureAwait(false);
        return found.IsSuccess ? ToList(found.Value) : found.Error;
    }

    public async Task<Result<TaskList>> CreateAsync(MailboxRef mailbox, string displayName, CancellationToken cancellationToken)
    {
        var principal = await collections.PrincipalAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!principal.IsSuccess)
        {
            return principal.Error;
        }

        if (principal.Value.CalendarHomeSet is not { } set || paths.ToRelative(set) is not { } home)
        {
            return Error.Forbidden("The mailbox has no calendar home for lists.");
        }

        var href = $"{home.TrimEnd('/')}/nightware-{Guid.NewGuid():N}/";
        var made = await dav.MkCalendarAsync(href, displayName, [DavTaskCollections.TaskComponent], null, cancellationToken).ConfigureAwait(false);
        return made.IsSuccess
            ? new TaskList(DavItemId.Resource(href).ToTaskListId(), displayName) { WellKnownListName = NoWellKnownName }
            : made.Error;
    }

    public async Task<Result<TaskList>> RenameAsync(MailboxRef mailbox, TaskListId list, string displayName, CancellationToken cancellationToken)
    {
        var found = await FindAsync(mailbox, list, cancellationToken).ConfigureAwait(false);
        if (!found.IsSuccess)
        {
            return found.Error;
        }

        var patched = await dav.PropPatchAsync(found.Value.Href, [new System.Xml.Linq.XElement(DavNames.DisplayName, displayName)], [], cancellationToken).ConfigureAwait(false);
        if (!patched.IsSuccess)
        {
            return patched.Error;
        }

        // Sabre answers 207 even when it refused the property; a refusal has a non-2xx propstat.
        return patched.Value.Responses.SelectMany(r => r.PropStats).Any(p => !p.IsSuccess)
            ? Error.Forbidden("The list cannot be renamed.")
            : ToList(found.Value) with { DisplayName = displayName };
    }

    public async Task<Result> DeleteAsync(MailboxRef mailbox, TaskListId list, CancellationToken cancellationToken)
    {
        var found = await FindAsync(mailbox, list, cancellationToken).ConfigureAwait(false);
        if (!found.IsSuccess)
        {
            return found.Error;
        }

        if (found.Value.Calendar.Components.Count != 1)
        {
            return Error.Validation("This list is also a calendar; delete it as a calendar instead.", "id");
        }

        return await dav.DeleteAsync(found.Value.Href, DavPrecondition.None, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<DavTaskCollection>> FindAsync(MailboxRef mailbox, TaskListId list, CancellationToken cancellationToken)
    {
        var lists = await collections.ListAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!lists.IsSuccess)
        {
            return lists.Error;
        }

        var match = lists.Value.FirstOrDefault(l => DavItemId.Resource(l.Href).ToTaskListId() == list);
        return match is null ? Error.NotFound("The specified object was not found in the store.", "id") : match;
    }

    private static TaskList ToList(DavTaskCollection l) => new(DavItemId.Resource(l.Href).ToTaskListId(), l.Calendar.DisplayName)
    {
        IsOwner = !l.Calendar.IsShared,
        IsShared = l.Calendar.IsShared,
        WellKnownListName = l.IsDefault ? DefaultListName : NoWellKnownName,
    };
}

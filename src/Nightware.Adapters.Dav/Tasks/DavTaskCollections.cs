using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Core.Collections;
using Nightware.Adapters.Dav.Core.Discovery;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Tasks;

/// <summary>A calendar collection that holds tasks (VTODO), as a To Do list.</summary>
/// <param name="Href">Relative to the DAV root, with a trailing slash (the form <see cref="DavItemId"/> encodes).</param>
internal sealed record DavTaskCollection(string Href, DavCalendar Calendar, bool IsDefault);

/// <summary>
/// Finds a mailbox's task lists and checks that ids name collections and tasks of that mailbox.
/// </summary>
/// <remarks>
/// Microsoft To Do always has a default list; Nextcloud users have none until the Tasks app (or
/// another client) creates a VTODO calendar. The first listing of the caller's own lists therefore
/// creates <c>tasks/</c> ("Tasks", VTODO only) when they own no task collection — the same
/// collection the Nextcloud Tasks app would create. The default list is the caller's own task
/// collection named <c>tasks</c>, else one that holds only tasks, else the first by calendar order
/// and name.
/// </remarks>
internal sealed class DavTaskCollections(IDavPrincipalDiscovery discovery, IDavCollectionCatalog catalog, IDavClient dav, DavPaths paths)
{
    public const string DefaultListSegment = "tasks";
    public const string DefaultListName = "Tasks";
    public const string TaskComponent = "VTODO";

    public async Task<Result<DavPrincipal>> PrincipalAsync(MailboxRef mailbox, CancellationToken cancellationToken) =>
        await discovery.DiscoverAsync(mailbox, cancellationToken).ConfigureAwait(false);

    public async Task<Result<IReadOnlyList<DavTaskCollection>>> ListAsync(MailboxRef mailbox, CancellationToken cancellationToken)
    {
        var principal = await PrincipalAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!principal.IsSuccess)
        {
            return principal.Error;
        }

        var listed = await ListAsync(principal.Value, cancellationToken).ConfigureAwait(false);
        if (!listed.IsSuccess || listed.Value.Any(c => !c.Calendar.IsShared) || !principal.Value.IsCurrentUser)
        {
            return listed;
        }

        var home = principal.Value.CalendarHomeSet is { } set ? paths.ToRelative(set) : null;
        if (home is null)
        {
            return listed;
        }

        var created = await dav.MkCalendarAsync(home + DefaultListSegment + "/", DefaultListName, [TaskComponent], null, cancellationToken).ConfigureAwait(false);
        if (!created.IsSuccess && created.Error.Kind != ErrorKind.Conflict)
        {
            return created.Error;
        }

        return await ListAsync(principal.Value, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The list's href (relative, trailing slash) when <paramref name="list"/> lies in the mailbox's calendar home.</summary>
    public async Task<Result<string>> ResolveListAsync(MailboxRef mailbox, TaskListId list, CancellationToken cancellationToken)
    {
        var id = list.ToDavItemId();
        if (!id.IsSuccess)
        {
            return id.Error;
        }

        var principal = await PrincipalAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!principal.IsSuccess)
        {
            return principal.Error;
        }

        var home = principal.Value.CalendarHomeSet is { } set ? paths.ToRelative(set) : null;
        return home is not null && id.Value.Kind == DavItemKind.Resource && id.Value.Href.EndsWith('/') && id.Value.IsWithin(home)
            ? id.Value.Href
            : NotFound("list");
    }

    /// <summary>The task's href (relative) when <paramref name="task"/> is a resource directly inside <paramref name="listHref"/>.</summary>
    public static Result<string> ResolveTask(string listHref, TaskId task)
    {
        var id = task.ToDavItemId();
        if (!id.IsSuccess)
        {
            return id.Error;
        }

        var href = id.Value.Href;
        return id.Value.Kind == DavItemKind.Resource && id.Value.IsWithin(listHref) && href.IndexOf('/', listHref.Length) < 0
            ? href
            : NotFound("id");
    }

    private static Error NotFound(string target) => Error.NotFound("The specified object was not found in the store.", target);

    private async Task<Result<IReadOnlyList<DavTaskCollection>>> ListAsync(DavPrincipal principal, CancellationToken cancellationToken)
    {
        var calendars = await catalog.ListCalendarsAsync(principal, cancellationToken).ConfigureAwait(false);
        if (!calendars.IsSuccess)
        {
            return calendars.Error;
        }

        var lists = calendars.Value
            .Where(c => c.SupportsTasks)
            .Select(c => (Href: paths.ToRelative(c.Href), Calendar: c))
            .Where(c => c.Href is not null)
            .ToList();
        var preferred = lists
            .Where(c => !c.Calendar.IsShared)
            .OrderBy(c => string.Equals(LastSegment(c.Href!), DefaultListSegment, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(c => c.Calendar.Components.Count == 1 ? 0 : 1)
            .ThenBy(c => c.Calendar.Order ?? int.MaxValue)
            .ThenBy(c => c.Calendar.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => c.Href)
            .FirstOrDefault();
        IReadOnlyList<DavTaskCollection> result = lists
            .Select(c => new DavTaskCollection(c.Href!, c.Calendar, string.Equals(c.Href, preferred, StringComparison.Ordinal)))
            .ToList();
        return Result<IReadOnlyList<DavTaskCollection>>.Success(result);
    }

    private static string LastSegment(string href) => href.TrimEnd('/').Split('/')[^1];
}

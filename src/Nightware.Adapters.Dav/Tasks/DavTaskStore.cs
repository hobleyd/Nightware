using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Tasks.ICal;
using Nightware.Application.Common.Query;
using Nightware.Application.Tasks;
using Nightware.Domain.Kernel;
using Nightware.Domain.Tasks;

namespace Nightware.Adapters.Dav.Tasks;

/// <summary>
/// <see cref="ITaskStore"/> over a VTODO calendar collection. One task is one <c>{uuid}.ics</c>
/// resource; updates edit the stored VTODO in place (<see cref="VTodoCodec.Apply"/>) under
/// <c>If-Match</c>, so properties NightMail does not model survive.
/// </summary>
internal sealed class DavTaskStore(DavTaskCollections collections, IDavClient dav, DavPaths paths, VTodoCodec codec) : ITaskStore
{
    internal const string ICalendarContentType = "text/calendar; charset=utf-8";

    public async Task<Result<Page<TodoTask>>> ListAsync(MailboxRef mailbox, TaskListId list, ItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var listHref = await collections.ResolveListAsync(mailbox, list, cancellationToken).ConfigureAwait(false);
        if (!listHref.IsSuccess)
        {
            return listHref.Error;
        }

        var report = await dav.ReportAsync(listHref.Value, DavDepth.One, DavRequestBodies.CalendarQuery(new CalendarQuery(DavTaskCollections.TaskComponent)), cancellationToken).ConfigureAwait(false);
        if (!report.IsSuccess)
        {
            return report.Error;
        }

        var tasks = new List<TodoTask>();
        foreach (var response in report.Value.Responses.Where(r => r.IsFound && r.CalendarData is not null))
        {
            // Anything Sabre lists outside the collection, or that does not parse, is not a task of this list.
            var href = paths.ToRelative(response.Href);
            if (href is null || !DavTaskCollections.ResolveTask(listHref.Value, DavItemId.Resource(href).ToTaskId()).IsSuccess)
            {
                continue;
            }

            var parsed = codec.Parse(response.CalendarData!, href);
            if (parsed is { IsSuccess: true, Value.Count: > 0 })
            {
                tasks.Add(parsed.Value[0].Task);
            }
        }

        return TaskQueryEvaluator.Apply(tasks.OrderBy(t => t.Id.Value, StringComparer.Ordinal), query);
    }

    public async Task<Result<TodoTask>> GetAsync(MailboxRef mailbox, TaskListId list, TaskId id, CancellationToken cancellationToken)
    {
        var href = await ResolveAsync(mailbox, list, id, cancellationToken).ConfigureAwait(false);
        if (!href.IsSuccess)
        {
            return href.Error;
        }

        var resource = await dav.GetAsync(href.Value, cancellationToken).ConfigureAwait(false);
        return resource.IsSuccess ? First(codec.Parse(resource.Value.Body, href.Value)) : resource.Error;
    }

    public async Task<Result<TodoTask>> CreateAsync(MailboxRef mailbox, TaskListId list, TaskDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var listHref = await collections.ResolveListAsync(mailbox, list, cancellationToken).ConfigureAwait(false);
        if (!listHref.IsSuccess)
        {
            return listHref.Error;
        }

        // Resource names come from [0-9a-f] only, so the href Sabre echoes is the one we encode (one id per task).
        var uid = Guid.NewGuid().ToString("N");
        var href = $"{listHref.Value}{uid}.ics";
        var ics = codec.Create(draft, uid);
        var put = await dav.PutAsync(href, ics, ICalendarContentType, DavPrecondition.CreateOnly, cancellationToken).ConfigureAwait(false);
        return put.IsSuccess ? First(codec.Parse(ics, href)) : put.Error;
    }

    public async Task<Result<TodoTask>> UpdateAsync(MailboxRef mailbox, TaskListId list, TaskId id, TaskDraft patch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var href = await ResolveAsync(mailbox, list, id, cancellationToken).ConfigureAwait(false);
        if (!href.IsSuccess)
        {
            return href.Error;
        }

        var edited = await EditAsync(href.Value, ics => codec.Apply(ics, patch), cancellationToken).ConfigureAwait(false);
        return edited.IsSuccess ? First(codec.Parse(edited.Value, href.Value)) : edited.Error;
    }

    public async Task<Result> DeleteAsync(MailboxRef mailbox, TaskListId list, TaskId id, CancellationToken cancellationToken)
    {
        var href = await ResolveAsync(mailbox, list, id, cancellationToken).ConfigureAwait(false);
        return href.IsSuccess
            ? await dav.DeleteAsync(href.Value, DavPrecondition.None, cancellationToken).ConfigureAwait(false)
            : href.Error;
    }

    /// <summary>The task's href (relative) after checking the list belongs to the mailbox and the task to the list.</summary>
    internal async Task<Result<string>> ResolveAsync(MailboxRef mailbox, TaskListId list, TaskId id, CancellationToken cancellationToken)
    {
        var listHref = await collections.ResolveListAsync(mailbox, list, cancellationToken).ConfigureAwait(false);
        return listHref.IsSuccess ? DavTaskCollections.ResolveTask(listHref.Value, id) : listHref.Error;
    }

    /// <summary>
    /// Read-modify-write of a stored task under <c>If-Match</c>. A concurrent change between the read
    /// and the write (412) is retried once on the fresh copy; the edited iCalendar is returned.
    /// </summary>
    internal async Task<Result<string>> EditAsync(string href, Func<string, Result<string>> edit, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var current = await dav.GetAsync(href, cancellationToken).ConfigureAwait(false);
            if (!current.IsSuccess)
            {
                return current.Error;
            }

            var edited = edit(current.Value.Body);
            if (!edited.IsSuccess)
            {
                return edited.Error;
            }

            var precondition = current.Value.ETag is { } etag ? DavPrecondition.Matching(etag) : DavPrecondition.None;
            var put = await dav.PutAsync(href, edited.Value, ICalendarContentType, precondition, cancellationToken).ConfigureAwait(false);
            if (put.IsSuccess)
            {
                return edited;
            }

            if (put.Error.Kind != ErrorKind.PreconditionFailed || attempt > 0)
            {
                return put.Error;
            }
        }
    }

    private static Result<TodoTask> First(Result<IReadOnlyList<ParsedTodo>> parsed) =>
        parsed.Bind(todos => todos.Count > 0
            ? Result<TodoTask>.Success(todos[0].Task)
            : Error.NotFound("The specified object was not found in the store."));
}

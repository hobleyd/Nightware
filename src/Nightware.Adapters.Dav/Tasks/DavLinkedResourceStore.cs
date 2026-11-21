using Nightware.Adapters.Dav.Tasks.ICal;
using Nightware.Application.Tasks;
using Nightware.Domain.Kernel;
using Nightware.Domain.Tasks;

namespace Nightware.Adapters.Dav.Tasks;

/// <summary>
/// <see cref="ILinkedResourceStore"/> over <c>X-NIGHTWARE-LINKED-RESOURCE</c> properties on the task's VTODO
/// (ADR 0006 D8): no standard property carries Graph's four members. Each write is a read-modify-write
/// of the task under <c>If-Match</c>.
/// </summary>
internal sealed class DavLinkedResourceStore(DavTaskStore tasks, Core.IDavClient dav, VTodoCodec codec) : ILinkedResourceStore
{
    public async Task<Result<IReadOnlyList<LinkedResource>>> ListAsync(MailboxRef mailbox, TaskListId list, TaskId task, CancellationToken cancellationToken)
    {
        var href = await tasks.ResolveAsync(mailbox, list, task, cancellationToken).ConfigureAwait(false);
        if (!href.IsSuccess)
        {
            return href.Error;
        }

        var resource = await dav.GetAsync(href.Value, cancellationToken).ConfigureAwait(false);
        if (!resource.IsSuccess)
        {
            return resource.Error;
        }

        var parsed = codec.Parse(resource.Value.Body, href.Value);
        return parsed is { IsSuccess: true, Value: [var todo, ..] }
            ? Result<IReadOnlyList<LinkedResource>>.Success(todo.LinkedResources)
            : Error.NotFound("The specified object was not found in the store.", "id");
    }

    public Task<Result<LinkedResource>> CreateAsync(MailboxRef mailbox, TaskListId list, TaskId task, LinkedResource resource, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var created = resource with { Id = Guid.NewGuid().ToString() };
        return EditAsync(mailbox, list, task, current => Result<List<LinkedResource>>.Success([.. current, created]), created.Id, cancellationToken);
    }

    public Task<Result<LinkedResource>> UpdateAsync(MailboxRef mailbox, TaskListId list, TaskId task, string id, LinkedResource patch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return EditAsync(mailbox, list, task, current =>
        {
            var index = current.FindIndex(r => r.Id == id);
            if (index < 0)
            {
                return NotFound();
            }

            var old = current[index];
            current[index] = old with
            {
                WebUrl = patch.WebUrl ?? old.WebUrl,
                ApplicationName = patch.ApplicationName ?? old.ApplicationName,
                DisplayName = patch.DisplayName ?? old.DisplayName,
                ExternalId = patch.ExternalId ?? old.ExternalId,
            };
            return current;
        }, id, cancellationToken);
    }

    public async Task<Result> DeleteAsync(MailboxRef mailbox, TaskListId list, TaskId task, string id, CancellationToken cancellationToken)
    {
        var edited = await EditAsync(mailbox, list, task, current =>
            current.Exists(r => r.Id == id) ? current.Where(r => r.Id != id).ToList() : NotFound(), null, cancellationToken).ConfigureAwait(false);
        return edited.IsSuccess ? Result.Success() : Result.Failure(edited.Error);
    }

    /// <param name="returned">The id to answer with after the write; null when nothing is returned.</param>
    private async Task<Result<LinkedResource>> EditAsync(
        MailboxRef mailbox, TaskListId list, TaskId task, Func<List<LinkedResource>, Result<List<LinkedResource>>> change, string? returned, CancellationToken cancellationToken)
    {
        var href = await tasks.ResolveAsync(mailbox, list, task, cancellationToken).ConfigureAwait(false);
        if (!href.IsSuccess)
        {
            return href.Error;
        }

        List<LinkedResource>? written = null;
        var edited = await tasks.EditAsync(href.Value, ics =>
        {
            var parsed = codec.Parse(ics, href.Value);
            if (parsed is not { IsSuccess: true, Value: [var todo, ..] })
            {
                return NotFound();
            }

            var next = change([.. todo.LinkedResources]);
            if (!next.IsSuccess)
            {
                return next.Error;
            }

            written = next.Value;
            return codec.SetLinkedResources(ics, next.Value);
        }, cancellationToken).ConfigureAwait(false);
        if (!edited.IsSuccess)
        {
            return edited.Error;
        }

        return returned is null ? new LinkedResource(string.Empty) : written!.First(r => r.Id == returned);
    }

    private static Error NotFound() => Error.NotFound("The specified object was not found in the store.", "id");
}

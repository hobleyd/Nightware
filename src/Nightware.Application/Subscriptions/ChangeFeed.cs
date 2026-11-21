using Nightware.Application.Contacts;
using Nightware.Application.Mail;
using Nightware.Application.Tasks;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;
using Nightware.Domain.Subscriptions;

namespace Nightware.Application.Subscriptions;

/// <summary>One change to report: the item's Graph id and what happened to it.</summary>
public sealed record ResourceChange(string Id, SubscriptionChangeType ChangeType);

/// <summary>A page of changes and the state to resume from once they have been delivered.</summary>
public sealed record ChangeBatch(IReadOnlyList<ResourceChange> Changes, string State);

/// <summary>Changes to the events of a mailbox's default calendar (no window, no expansion), by an adapter's own state.</summary>
public interface IEventChangeFeed
{
    Task<Result<string>> BaselineAsync(MailboxRef mailbox, CancellationToken cancellationToken);

    /// <param name="since">Items created after this are reported as created, older ones as updated.</param>
    Task<Result<ChangeBatch>> ChangesAsync(MailboxRef mailbox, string state, DateTimeOffset since, CancellationToken cancellationToken);
}

/// <summary>
/// What changed in a subscription's resource since its state, over the same delta ports clients use. Delta does not
/// say whether an item is new, so an item created after the previous poll (less <see cref="CreatedTolerance"/>) is
/// reported as created and any other as updated (a heuristic; see the phase 16 doc). Runs as the subscriber: in a
/// request, or in the notifier's scope.
/// </summary>
public sealed class ChangeFeed(IMailDeltaReader mail, IContactFolderStore contactFolders, IContactDelta contacts, ITaskDelta tasks, IEventChangeFeed events)
{
    /// <summary>
    /// Backends keep whole seconds (Stalwart's receivedAt) and their clocks may differ slightly from this host's, so
    /// "created after the last poll" allows this much: an item made in the same second as the poll is still new.
    /// </summary>
    public static readonly TimeSpan CreatedTolerance = TimeSpan.FromSeconds(2);

    private static readonly DeltaRequest Page = new(DeltaRequest.DefaultPageSize, new HashSet<string>(StringComparer.Ordinal) { "id", "createdDateTime", "receivedDateTime" });

    /// <summary>The state as of now, without reporting what already exists.</summary>
    public async Task<Result<string>> BaselineAsync(SubscriptionTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Kind == SubscriptionResourceKind.Events)
        {
            return await events.BaselineAsync(target.Mailbox, cancellationToken).ConfigureAwait(false);
        }

        var batch = await DrainAsync(target, null, DateTimeOffset.MaxValue, cancellationToken).ConfigureAwait(false);
        return batch.IsSuccess ? batch.Value.State : batch.Error;
    }

    public Task<Result<ChangeBatch>> ChangesAsync(SubscriptionTarget target, string state, DateTimeOffset since, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(state);
        return target.Kind == SubscriptionResourceKind.Events
            ? events.ChangesAsync(target.Mailbox, state, since, cancellationToken)
            : DrainAsync(target, new DeltaToken(state), since, cancellationToken);
    }

    /// <summary>The folder a mail subscription names: a well-known name (<c>Inbox</c>) or an id.</summary>
    public static FolderRef MailFolder(string folder) =>
        Enum.TryParse<WellKnownFolder>(folder, ignoreCase: true, out var known) ? new FolderRef.WellKnown(known) : new FolderRef.ById(new FolderId(folder));

    /// <summary>Follows next pages to the final token, collecting what changed.</summary>
    private async Task<Result<ChangeBatch>> DrainAsync(SubscriptionTarget target, DeltaToken? token, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var changes = new List<ResourceChange>();
        for (var pages = 0; pages < 1000; pages++)
        {
            var page = await PageAsync(target, token, since, cancellationToken).ConfigureAwait(false);
            if (!page.IsSuccess)
            {
                return page.Error;
            }

            changes.AddRange(page.Value.Changes);
            if (page.Value.Final is { } final)
            {
                return new ChangeBatch(changes, final.Value);
            }

            token = page.Value.Next;
        }

        return Error.BackendUnavailable("The change feed did not settle.");
    }

    private async Task<Result<(List<ResourceChange> Changes, DeltaToken? Next, DeltaToken? Final)>> PageAsync(
        SubscriptionTarget target, DeltaToken? token, DateTimeOffset since, CancellationToken cancellationToken)
    {
        switch (target.Kind)
        {
            case SubscriptionResourceKind.Messages when target.Folder is { } folder:
                var messages = await mail.DeltaAsync(target.Mailbox, MailFolder(folder), token, Page, cancellationToken).ConfigureAwait(false);
                return messages.IsSuccess ? Map(messages.Value, m => m.Id.Value, m => m.CreatedDateTime ?? m.ReceivedDateTime, since) : messages.Error;
            case SubscriptionResourceKind.Contacts:
                var book = await contactFolders.GetAsync(target.Mailbox, "contacts", cancellationToken).ConfigureAwait(false);
                if (!book.IsSuccess)
                {
                    return book.Error;
                }

                var contactPage = await contacts.DeltaAsync(target.Mailbox, book.Value.Id, token, cancellationToken).ConfigureAwait(false);
                return contactPage.IsSuccess ? Map(contactPage.Value, c => c.Id.Value, c => c.CreatedDateTime, since) : contactPage.Error;
            case SubscriptionResourceKind.Tasks when target.Folder is { } list:
                var taskPage = await tasks.DeltaAsync(target.Mailbox, new TaskListId(list), token, cancellationToken).ConfigureAwait(false);
                return taskPage.IsSuccess ? Map(taskPage.Value, t => t.Id.Value, t => t.CreatedDateTime, since) : taskPage.Error;
            default:
                return Error.Unsupported("Change notifications for this resource are not supported.");
        }
    }

    private static (List<ResourceChange>, DeltaToken?, DeltaToken?) Map<T>(DeltaPage<T> page, Func<T, string> id, Func<T, DateTimeOffset?> created, DateTimeOffset since)
    {
        var changes = page.Entries.Select(entry => entry switch
        {
            DeltaEntry<T>.Upsert upsert => new ResourceChange(id(upsert.Item), created(upsert.Item) > since - CreatedTolerance ? SubscriptionChangeType.Created : SubscriptionChangeType.Updated),
            DeltaEntry<T>.Removed removed => new ResourceChange(removed.Id, SubscriptionChangeType.Deleted),
            _ => throw new InvalidOperationException("Unknown delta entry."),
        }).ToList();
        return (changes, page.NextPage, page.Final);
    }
}

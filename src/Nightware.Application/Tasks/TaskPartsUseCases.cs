using Nightware.Domain.Kernel;
using Nightware.Domain.Tasks;

namespace Nightware.Application.Tasks;

/// <summary><c>…/tasks/{id}/checklistItems</c> (ADR 0006 D8): a task's steps, which Nextcloud Tasks shows as subtasks.</summary>
public sealed class ChecklistItemsUseCase(IChecklistStore checklist)
{
    public Task<Result<IReadOnlyList<ChecklistItem>>> ListAsync(MailboxRef mailbox, TaskListId list, TaskId task, CancellationToken cancellationToken) =>
        checklist.ListAsync(mailbox, list, task, cancellationToken);

    public Task<Result<ChecklistItem>> CreateAsync(MailboxRef mailbox, TaskListId list, TaskId task, string? displayName, bool? isChecked, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(displayName)
            ? Task.FromResult<Result<ChecklistItem>>(Error.Validation("displayName is required.", "displayName"))
            : checklist.CreateAsync(mailbox, list, task, displayName.Trim(), isChecked ?? false, cancellationToken);

    public Task<Result<ChecklistItem>> UpdateAsync(
        MailboxRef mailbox, TaskListId list, TaskId task, string item, string? displayName, bool? isChecked, CancellationToken cancellationToken) =>
        displayName is not null && string.IsNullOrWhiteSpace(displayName)
            ? Task.FromResult<Result<ChecklistItem>>(Error.Validation("displayName cannot be empty.", "displayName"))
            : checklist.UpdateAsync(mailbox, list, task, item, displayName?.Trim(), isChecked, cancellationToken);

    public Task<Result> DeleteAsync(MailboxRef mailbox, TaskListId list, TaskId task, string item, CancellationToken cancellationToken) =>
        checklist.DeleteAsync(mailbox, list, task, item, cancellationToken);
}

/// <summary><c>…/tasks/{id}/linkedResources</c> (ADR 0006 D8).</summary>
public sealed class LinkedResourcesUseCase(ILinkedResourceStore resources)
{
    public Task<Result<IReadOnlyList<LinkedResource>>> ListAsync(MailboxRef mailbox, TaskListId list, TaskId task, CancellationToken cancellationToken) =>
        resources.ListAsync(mailbox, list, task, cancellationToken);

    public Task<Result<LinkedResource>> CreateAsync(MailboxRef mailbox, TaskListId list, TaskId task, LinkedResource resource, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);
        return Validate(resource) is { } invalid
            ? Task.FromResult<Result<LinkedResource>>(invalid)
            : resources.CreateAsync(mailbox, list, task, resource, cancellationToken);
    }

    public Task<Result<LinkedResource>> UpdateAsync(MailboxRef mailbox, TaskListId list, TaskId task, string id, LinkedResource patch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return Validate(patch) is { } invalid
            ? Task.FromResult<Result<LinkedResource>>(invalid)
            : resources.UpdateAsync(mailbox, list, task, id, patch, cancellationToken);
    }

    public Task<Result> DeleteAsync(MailboxRef mailbox, TaskListId list, TaskId task, string id, CancellationToken cancellationToken) =>
        resources.DeleteAsync(mailbox, list, task, id, cancellationToken);

    private static Error? Validate(LinkedResource resource) =>
        resource.WebUrl is { Length: > 0 } url && !(Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            ? Error.Validation("webUrl must be an absolute http(s) URL.", "webUrl")
            : null;
}

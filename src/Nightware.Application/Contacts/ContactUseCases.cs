using Nightware.Application.Calendar;
using Nightware.Application.MailboxSettings;
using Nightware.Domain.Contacts;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Contacts;

/// <summary><c>GET {mailbox}/contacts/{id}</c>.</summary>
public sealed class GetContact(IContactStore contacts)
{
    public Task<Result<Contact>> ExecuteAsync(MailboxRef mailbox, ContactId id, CancellationToken cancellationToken) =>
        contacts.GetAsync(mailbox, id, cancellationToken);
}

/// <summary><c>POST {mailbox}/contacts</c> (the default folder) and <c>POST contactFolders/{id}/contacts</c>.</summary>
/// <remarks>A <c>contactsOnly</c> automatic reply of the mailbox follows the change (<see cref="RefreshAutoReplyKnownSenders"/>).</remarks>
public sealed class CreateContact(IContactStore contacts, RefreshAutoReplyKnownSenders? replies = null)
{
    public async Task<Result<Contact>> ExecuteAsync(MailboxRef mailbox, ContactFolderId? folder, ContactDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (ContactRules.Validate(draft) is { } invalid)
        {
            return invalid;
        }

        var created = await contacts.CreateAsync(mailbox, folder, draft, cancellationToken).ConfigureAwait(false);
        await ContactChanges.FollowAsync(replies, mailbox, created.IsSuccess, cancellationToken).ConfigureAwait(false);
        return created;
    }
}

/// <summary>Lets the mailbox's automatic reply follow a contact change; never fails the change.</summary>
internal static class ContactChanges
{
    public static async Task FollowAsync(RefreshAutoReplyKnownSenders? replies, MailboxRef mailbox, bool changed, CancellationToken cancellationToken)
    {
        if (replies is null || !changed)
        {
            return;
        }

        try
        {
            await replies.ExecuteAsync(mailbox, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The contact changed; a reply that couldn't follow keeps its last list until the next change or save.
        }
    }
}

/// <summary><c>PATCH {mailbox}/contacts/{id}</c>: only the members the request named change.</summary>
public sealed class UpdateContact(IContactStore contacts, RefreshAutoReplyKnownSenders? replies = null)
{
    public async Task<Result<Contact>> ExecuteAsync(MailboxRef mailbox, ContactId id, ContactDraft patch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        if (ContactRules.Validate(patch) is { } invalid)
        {
            return invalid;
        }

        var updated = await contacts.UpdateAsync(mailbox, id, patch, cancellationToken).ConfigureAwait(false);
        await ContactChanges.FollowAsync(replies, mailbox, updated.IsSuccess && patch.EmailAddresses is not null, cancellationToken).ConfigureAwait(false);
        return updated;
    }
}

/// <summary><c>DELETE {mailbox}/contacts/{id}</c>.</summary>
public sealed class DeleteContact(IContactStore contacts, RefreshAutoReplyKnownSenders? replies = null)
{
    public async Task<Result> ExecuteAsync(MailboxRef mailbox, ContactId id, CancellationToken cancellationToken)
    {
        var deleted = await contacts.DeleteAsync(mailbox, id, cancellationToken).ConfigureAwait(false);
        await ContactChanges.FollowAsync(replies, mailbox, deleted.IsSuccess, cancellationToken).ConfigureAwait(false);
        return deleted;
    }
}

/// <summary><c>GET {mailbox}/contacts/{id}/photo/$value</c>.</summary>
public sealed class GetContactPhoto(IContactStore contacts)
{
    public Task<Result<BlobContent>> ExecuteAsync(MailboxRef mailbox, ContactId id, CancellationToken cancellationToken) =>
        contacts.GetPhotoAsync(mailbox, id, cancellationToken);
}

/// <summary><c>PUT {mailbox}/contacts/{id}/photo/$value</c>: an image of at most 4 MB (Graph's limit).</summary>
public sealed class SetContactPhoto(IContactStore contacts)
{
    public const int MaxBytes = 4 * 1024 * 1024;

    public Task<Result> ExecuteAsync(MailboxRef mailbox, ContactId id, byte[] photo, string? contentType, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(photo);
        if (photo.Length == 0)
        {
            return Task.FromResult<Result>(Error.Validation("The photo is empty."));
        }

        if (photo.Length > MaxBytes)
        {
            return Task.FromResult<Result>(Error.Validation($"The photo is larger than {MaxBytes / (1024 * 1024)} MB."));
        }

        var type = string.IsNullOrWhiteSpace(contentType) ? "image/jpeg" : contentType.Split(';')[0].Trim().ToLowerInvariant();
        return type.StartsWith("image/", StringComparison.Ordinal)
            ? contacts.SetPhotoAsync(mailbox, id, photo, type, cancellationToken)
            : Task.FromResult<Result>(Error.Validation("The photo must be an image.", "Content-Type"));
    }
}

internal static class ContactRules
{
    public static Error? Validate(ContactDraft draft) =>
        draft.EmailAddresses?.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.Address) && !CalendarRules.IsAddress(e.Address.Trim())) is { } bad
            ? Error.Validation($"'{bad.Address}' is not a valid email address.", "emailAddresses")
            : null;
}

using Nightware.Application.Common.Query;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Directory;

public interface IUserDirectory
{
    /// <param name="idOrUpn">A directory id, UPN or primary SMTP address.</param>
    Task<Result<DirectoryUser>> GetAsync(string idOrUpn, CancellationToken cancellationToken);

    Task<Result<Page<DirectoryUser>>> ListAsync(ItemQuery query, CancellationToken cancellationToken);
}

/// <summary>
/// The shared mailboxes the signed-in user may open, seen as directory entries (phase 10): NightMail reads a shared
/// mailbox's profile through <c>/users/{address}</c>, and a shared mailbox is nobody in the directory.
/// </summary>
public interface ISharedMailboxDirectory
{
    /// <summary>NotFound when the signed-in user cannot open a shared mailbox at <paramref name="address"/>.</summary>
    Task<Result<DirectoryUser>> GetAsync(string address, CancellationToken cancellationToken);
}

/// <summary>
/// The addresses in the signed-in user's organizations that are nobody's own mailbox and the user may send to: groups'
/// lists (a members-only list only to its members) and shared mailboxes.
/// </summary>
public interface IDirectoryAddresses
{
    Task<Result<IReadOnlyList<DirectoryAddress>>> ListAsync(CancellationToken cancellationToken);
}

public interface IUserPhotoReader
{
    /// <summary>The user's photo; <see cref="ErrorKind.NotFound"/> when they have none.</summary>
    Task<Result<BlobContent>> GetAsync(string idOrUpn, CancellationToken cancellationToken);
}

/// <summary>Sets or removes the signed-in user's own photo (phase 19). The store acts as the caller, so it can't reach anyone else's.</summary>
public interface IUserPhotoWriter
{
    /// <param name="image">A square JPEG or PNG (checked by the use case).</param>
    Task<Result> SetAsync(MailboxRef owner, ReadOnlyMemory<byte> image, string contentType, CancellationToken cancellationToken);

    Task<Result> DeleteAsync(MailboxRef owner, CancellationToken cancellationToken);
}

/// <summary>Mailbox preferences (time zone, language, working hours) stored as directory user attributes.</summary>
public interface IUserPreferencesStore
{
    Task<Result<UserPreferences>> GetAsync(MailboxRef mailbox, CancellationToken cancellationToken);

    Task<Result<UserPreferences>> SetAsync(MailboxRef mailbox, UserPreferencesPatch patch, CancellationToken cancellationToken);
}

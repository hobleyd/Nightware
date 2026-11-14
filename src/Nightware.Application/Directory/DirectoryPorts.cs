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

public interface IUserPhotoReader
{
    /// <summary>The user's photo; <see cref="ErrorKind.NotFound"/> when they have none.</summary>
    Task<Result<BlobContent>> GetAsync(string idOrUpn, CancellationToken cancellationToken);
}

/// <summary>Mailbox preferences (time zone, language, working hours) stored as directory user attributes.</summary>
public interface IUserPreferencesStore
{
    Task<Result<UserPreferences>> GetAsync(MailboxRef mailbox, CancellationToken cancellationToken);

    Task<Result<UserPreferences>> SetAsync(MailboxRef mailbox, UserPreferencesPatch patch, CancellationToken cancellationToken);
}

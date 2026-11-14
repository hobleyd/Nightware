using Nightware.Domain.Kernel;

namespace Nightware.Application.Directory;

/// <summary>
/// <c>GET /users/{id|upn}/photo/$value</c>: a user's own photo. <see cref="ErrorKind.NotFound"/> when
/// they have none, so the client draws initials instead.
/// </summary>
/// <remarks>
/// Photos are keyed by mailbox address. A directory id is resolved to the user's address first, so both
/// forms of <c>/users/{x}</c> reach the same photo.
/// </remarks>
public sealed class GetUserPhoto(IUserDirectory directory, IUserPhotoReader photos)
{
    public async Task<Result<BlobContent>> ExecuteAsync(string idOrUpn, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(idOrUpn);
        var key = idOrUpn.Trim();
        if (key.Length == 0)
        {
            return DirectoryUserLookup.Miss(idOrUpn);
        }

        if (DirectoryUserLookup.IsAddress(key))
        {
            return await photos.GetAsync(key.ToLowerInvariant(), cancellationToken).ConfigureAwait(false);
        }

        var user = await directory.GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (!user.IsSuccess)
        {
            return user.Error.Kind == ErrorKind.Validation ? DirectoryUserLookup.Miss(key) : user.Error;
        }

        var address = user.Value.Mail ?? (DirectoryUserLookup.IsAddress(user.Value.UserPrincipalName) ? user.Value.UserPrincipalName : null);
        return address is null
            ? Error.NotFound($"The user '{key}' has no mailbox, so no photo.")
            : await photos.GetAsync(address.ToLowerInvariant(), cancellationToken).ConfigureAwait(false);
    }
}

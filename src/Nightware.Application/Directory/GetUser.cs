using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Directory;

/// <summary>
/// <c>GET /users/{id|upn}</c>: one directory user, by directory id, UPN or SMTP address. Used for the
/// client's contact card and, through the mailbox base, for a shared mailbox's profile.
/// </summary>
public sealed class GetUser(IUserDirectory directory)
{
    public async Task<Result<DirectoryUser>> ExecuteAsync(string idOrUpn, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(idOrUpn);
        var key = idOrUpn.Trim();
        if (key.Length == 0)
        {
            return DirectoryUserLookup.Miss(idOrUpn);
        }

        var found = await directory.GetAsync(key, cancellationToken).ConfigureAwait(false);

        // A malformed id is just another name nobody has (see DirectoryUserLookup.Miss).
        return found.IsSuccess || found.Error.Kind != ErrorKind.Validation ? found : DirectoryUserLookup.Miss(key);
    }
}

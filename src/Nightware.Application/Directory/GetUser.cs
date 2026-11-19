using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Directory;

/// <summary>
/// <c>GET /users/{id|upn}</c>: one directory user, by directory id, UPN or SMTP address. Used for the
/// client's contact card and, through the mailbox base, for a shared mailbox's profile: an address the
/// directory does not know is looked up among the shared mailboxes the caller may open.
/// </summary>
public sealed class GetUser(IUserDirectory directory, ISharedMailboxDirectory sharedMailboxes)
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
        if (!found.IsSuccess && found.Error.Kind == ErrorKind.Validation)
        {
            return DirectoryUserLookup.Miss(key);
        }

        if (found.IsSuccess || found.Error.Kind != ErrorKind.NotFound || !DirectoryUserLookup.IsAddress(key))
        {
            return found;
        }

        // Not a shared mailbox the caller may open (or no mail backend to ask): still the directory's own miss.
        var shared = await sharedMailboxes.GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (shared.IsSuccess)
        {
            return shared;
        }

        return shared.Error.Kind is ErrorKind.NotFound or ErrorKind.Forbidden or ErrorKind.Unsupported ? found : shared;
    }
}

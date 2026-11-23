using Nightware.Application.Directory;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Talk;

/// <summary>
/// Talk names people by Nextcloud uid, Graph by directory id (ADR 0009 D2). Both directions go through
/// <see cref="IUserAccountNames"/>, which memoises; this adds the per-request rules: someone the directory doesn't
/// know is reported by display name alone, and a directory outage fails the request rather than dropping people.
/// </summary>
internal sealed class TalkPeople(IUserAccountNames names)
{
    /// <summary>The directory user behind a uid; null for someone the directory doesn't know (a guest, a bot).</summary>
    public async Task<Result<DirectoryUser?>> UserOfAsync(string uid, CancellationToken cancellationToken)
    {
        var user = await names.UserOfAccountAsync(uid, cancellationToken).ConfigureAwait(false);
        if (user.IsSuccess)
        {
            return user.Value;
        }

        return user.Error.Kind == ErrorKind.NotFound ? Result<DirectoryUser?>.Success(null) : user.Error;
    }

    /// <summary>The uid of a directory user named by id, UPN or address; NotFound for nobody in the directory.</summary>
    public Task<Result<string>> UidOfAsync(string user, CancellationToken cancellationToken) =>
        names.AccountNameOfAsync(user, cancellationToken);
}

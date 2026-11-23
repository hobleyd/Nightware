using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Directory;

/// <summary>
/// A directory user's account name on the groupware (the Nextcloud uid, which is the Keycloak username) and back
/// (ADR 0009 D2). Chats, teams and presence name people by directory id over Graph and by account name in Talk.
/// </summary>
public interface IUserAccountNames
{
    /// <param name="user">A directory id, UPN or primary SMTP address, as <c>user@odata.bind</c> may name one.</param>
    /// <returns>The account name; <see cref="ErrorKind.NotFound"/> for nobody in the directory.</returns>
    Task<Result<string>> AccountNameOfAsync(string user, CancellationToken cancellationToken);

    /// <returns>The person with that account name; <see cref="ErrorKind.NotFound"/> for nobody in the directory.</returns>
    Task<Result<DirectoryUser>> UserOfAccountAsync(string accountName, CancellationToken cancellationToken);
}

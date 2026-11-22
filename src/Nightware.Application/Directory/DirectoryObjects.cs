using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Directory;

/// <summary>Shared rules for the directory's relationships (phase 20): whose they are, and what a miss says.</summary>
internal static class DirectoryObjects
{
    /// <summary>Graph's code when a directory object or relationship is absent (a manager not set, an unknown group).</summary>
    public const string ResourceNotFoundCode = "Request_ResourceNotFound";

    public static Error ResourceNotFound(string resource) =>
        new(ErrorKind.NotFound, ResourceNotFoundCode, $"Resource '{resource}' does not exist or one of its queried reference-property objects are not present.");

    /// <summary>
    /// The directory id of the user a route names: the caller for <c>/me</c> (<paramref name="idOrUpn"/> null), else the
    /// user <c>/users/{x}</c> names, looked up as <c>GET /users/{x}</c> is (any miss is a 404).
    /// </summary>
    public static async Task<Result<string>> UserIdAsync(IUserContext user, IUserDirectory directory, string? idOrUpn, CancellationToken cancellationToken)
    {
        if (idOrUpn is null)
        {
            return user.Principal.Id;
        }

        var key = idOrUpn.Trim();
        if (key.Length == 0)
        {
            return DirectoryUserLookup.Miss(idOrUpn);
        }

        var found = await directory.GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (found.IsSuccess)
        {
            return found.Value.Id;
        }

        return found.Error.Kind is ErrorKind.Validation or ErrorKind.NotFound ? DirectoryUserLookup.Miss(key) : found.Error;
    }

    /// <summary>A NotFound from the store becomes Graph's directory miss; anything else is passed on.</summary>
    public static Result<T> OrMiss<T>(Result<T> result, string resource) =>
        result.IsSuccess || result.Error.Kind != ErrorKind.NotFound ? result : ResourceNotFound(resource);
}

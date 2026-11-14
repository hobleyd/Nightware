using Nightware.Domain.Kernel;

namespace Nightware.Application.Directory;

/// <summary>Shared rules for naming one directory user (<c>/users/{id|upn}</c>).</summary>
internal static class DirectoryUserLookup
{
    /// <summary>
    /// A lookup that names nobody answers 404, whatever the backend said: the client treats 404 (and
    /// 403) as "no card" silently, while a 400 would surface as an error.
    /// </summary>
    public static Error Miss(string idOrUpn) => Error.NotFound($"The user '{idOrUpn}' was not found in the directory.");

    public static bool IsAddress(string idOrUpn) => idOrUpn.Contains('@', StringComparison.Ordinal);
}

using System.Buffers.Text;
using System.Text;

using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Core.Discovery;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Files.Drive;

/// <summary>The caller's Nextcloud home: their uid, its Files href, and the drive id derived from it (ADR 0008 D1).</summary>
/// <param name="Uid">The uid segment exactly as the principal href carries it (still percent-encoded).</param>
internal sealed record DriveHome(string Uid)
{
    public const string DriveIdPrefix = "nc!";

    /// <summary>The home's href relative to the DAV root, <c>files/{uid}/</c>.</summary>
    public string Href => $"files/{Uid}/";

    /// <summary>Where chunked uploads are staged, <c>uploads/{uid}/</c>.</summary>
    public string UploadsHref => $"uploads/{Uid}/";

    public string DriveId => DriveIdPrefix + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(Uri.UnescapeDataString(Uid)));

    /// <summary>The path below the home (decoded, leading <c>/</c>; <c>/</c> for the home) of an href inside it; null outside.</summary>
    public string? PathOf(string relativeHref)
    {
        if (!relativeHref.StartsWith(Href, StringComparison.Ordinal) && relativeHref.TrimEnd('/') != Href.TrimEnd('/'))
        {
            return null;
        }

        var below = relativeHref.Length > Href.Length ? relativeHref[Href.Length..].TrimEnd('/') : string.Empty;
        return "/" + string.Join('/', below.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString));
    }
}

/// <summary>Finds the caller's <see cref="DriveHome"/> once per request scope.</summary>
internal sealed class DriveHomeLocator(IDavPrincipalDiscovery discovery, DavPaths paths)
{
    private Task<Result<DriveHome>>? _home;

    public Task<Result<DriveHome>> CurrentAsync(CancellationToken cancellationToken) =>
        _home is { IsCompletedSuccessfully: true } cached ? cached : _home = FindAsync(cancellationToken);

    /// <summary>The caller's home, provided <paramref name="driveId"/> (when given) names it; another drive is NotFound.</summary>
    public async Task<Result<DriveHome>> ForDriveAsync(string? driveId, CancellationToken cancellationToken)
    {
        var home = await CurrentAsync(cancellationToken).ConfigureAwait(false);
        return !home.IsSuccess || driveId is null || string.Equals(driveId, home.Value.DriveId, StringComparison.Ordinal)
            ? home
            : Error.NotFound("The drive does not exist, or it is not yours.");
    }

    private async Task<Result<DriveHome>> FindAsync(CancellationToken cancellationToken)
    {
        var principal = await discovery.CurrentUserAsync(cancellationToken).ConfigureAwait(false);
        if (!principal.IsSuccess)
        {
            return principal.Error;
        }

        return paths.ToRelative(principal.Value.Href)?.TrimEnd('/').Split('/') is ["principals", "users", { Length: > 0 } uid]
            ? new DriveHome(uid)
            : Error.NotFound("Only user accounts have a drive.");
    }
}

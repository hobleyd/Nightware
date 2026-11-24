using System.Globalization;
using System.Xml.Linq;

using Nightware.Adapters.Dav.Core;
using Nightware.Application.Files.Drives;
using Nightware.Domain.Files;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Files.Drive;

/// <summary>
/// <see cref="IDriveVersions"/> over Nextcloud's <c>files_versions</c> DAV: <c>versions/{uid}/versions/{fileid}/</c> lists
/// a file's versions (the current content among them), each named by its timestamp; a GET reads one, and a MOVE to
/// <c>versions/{uid}/restore/target</c> restores it (Nextcloud keeps the replaced content as a version).
/// </summary>
/// <remarks>
/// The current version is the one named by the file's modification time, not the newest: a restore gives the file the
/// restored version's time and leaves the listing as it was (verified 2026-11-24). It is listed first, the rest newest
/// first, as Graph lists the current version first.
/// </remarks>
internal sealed class NextcloudDriveVersions(IDavClient dav, DriveItemLocator locator, DavPaths paths) : IDriveVersions
{
    internal static readonly XName Label = DriveItemMapping.FilesNs + "version-label";
    internal static readonly XName Author = DriveItemMapping.FilesNs + "version-author";

    private static readonly XName[] Properties = [DavNames.GetLastModified, DavNames.GetContentLength, Label, Author];

    public async Task<Result<IReadOnlyList<DriveItemVersion>>> ListAsync(DriveItemRef item, CancellationToken cancellationToken)
    {
        var folder = await VersionsOfAsync(item, cancellationToken).ConfigureAwait(false);
        if (!folder.IsSuccess)
        {
            return folder.Error;
        }

        var listed = await dav.PropFindAsync(folder.Value.Href, DavDepth.One, Properties, cancellationToken).ConfigureAwait(false);
        if (!listed.IsSuccess)
        {
            return listed.Error;
        }

        var current = folder.Value.CurrentId;
        return listed.Value.Responses
            .Where(static r => r.IsFound)
            .Select(r => ToVersion(paths.ToRelative(r.Href) ?? r.Href, r))
            .OfType<DriveItemVersion>()
            .OrderByDescending(v => v.Id == current)
            .ThenByDescending(static v => long.Parse(v.Id, NumberStyles.None, CultureInfo.InvariantCulture))
            .ToList();
    }

    public async Task<Result<BlobContent>> OpenContentAsync(DriveItemRef item, string versionId, CancellationToken cancellationToken)
    {
        var href = await VersionHrefAsync(item, versionId, cancellationToken).ConfigureAwait(false);
        if (!href.IsSuccess)
        {
            return href.Error;
        }

        var download = await dav.OpenReadAsync(href.Value, cancellationToken).ConfigureAwait(false);
        if (!download.IsSuccess)
        {
            return download.Error;
        }

        Stream body = new ResponseStream(download.Value.Content, download.Value);
        return new BlobContent(download.Value.ContentType, download.Value.Length, _ => Task.FromResult(body));
    }

    public async Task<Result> RestoreAsync(DriveItemRef item, string versionId, CancellationToken cancellationToken)
    {
        var href = await VersionHrefAsync(item, versionId, cancellationToken).ConfigureAwait(false);
        if (!href.IsSuccess)
        {
            return href.Error;
        }

        var home = await locator.HomeAsync(item, cancellationToken).ConfigureAwait(false);
        return home.IsSuccess
            ? await dav.MoveAsync(href.Value, $"versions/{home.Value.Uid}/restore/target", overwrite: true, cancellationToken).ConfigureAwait(false)
            : home.Error;
    }

    public async Task<Result> CheckOutAsync(DriveItemRef item, CancellationToken cancellationToken)
    {
        var file = await FileAsync(item, cancellationToken).ConfigureAwait(false);
        if (!file.IsSuccess)
        {
            return file.Error;
        }

        var locked = await dav.UserLockAsync(file.Value.Href, locked: true, cancellationToken).ConfigureAwait(false);
        return locked.IsSuccess || locked.Error.Kind != ErrorKind.Conflict
            ? locked
            : new Error(ErrorKind.Conflict, LockedCode, "The item is checked out by someone else.");
    }

    public async Task<Result> CheckInAsync(DriveItemRef item, string? comment, CancellationToken cancellationToken)
    {
        var file = await FileAsync(item, cancellationToken).ConfigureAwait(false);
        if (!file.IsSuccess)
        {
            return file.Error;
        }

        var unlocked = await dav.UserLockAsync(file.Value.Href, locked: false, cancellationToken).ConfigureAwait(false);
        if (!unlocked.IsSuccess)
        {
            return unlocked.Error.Kind switch
            {
                ErrorKind.PreconditionFailed => Error.Validation("The item isn't checked out."),
                ErrorKind.Conflict => new Error(ErrorKind.Forbidden, LockedCode, "The item is checked out by someone else."),
                _ => unlocked.Error,
            };
        }

        if (comment is null)
        {
            return Result.Success();
        }

        // The comment labels the version being checked in: the current one, listed first.
        var versions = await ListAsync(item, cancellationToken).ConfigureAwait(false);
        if (!versions.IsSuccess || versions.Value.Count == 0)
        {
            return versions.IsSuccess ? Result.Success() : versions.Error;
        }

        var labelled = await dav.PropPatchAsync($"versions/{file.Value.Uid}/versions/{file.Value.FileId}/{versions.Value[0].Id}", [new XElement(Label, comment)], [], cancellationToken)
            .ConfigureAwait(false);
        return labelled.IsSuccess ? Result.Success() : labelled.Error;
    }

    /// <summary>Graph's code for an item checked out by someone else.</summary>
    internal const string LockedCode = "resourceLocked";

    private async Task<Result<(string Href, string Uid, string FileId)>> FileAsync(DriveItemRef item, CancellationToken cancellationToken)
    {
        var home = await locator.HomeAsync(item, cancellationToken).ConfigureAwait(false);
        if (!home.IsSuccess)
        {
            return home.Error;
        }

        var located = await locator.LocateAsync(home.Value, item, cancellationToken).ConfigureAwait(false);
        if (!located.IsSuccess)
        {
            return located.Error;
        }

        if (located.Value.IsFolder || located.Value.FileId is not { Length: > 0 } fileId)
        {
            return DriveErrors.NotSupported("A folder can't be checked out or in.");
        }

        // Without files_lock (shipped and enabled by default) Nextcloud reports nc:lock as unknown, and has no user locks.
        return located.Value.Response.StatusOf(DriveItemMapping.Lock) == 404
            ? new Error(ErrorKind.Unsupported, DriveErrors.NotSupportedCode, "Check-out needs Nextcloud's files_lock app, which is not enabled.")
            : (located.Value.Href, home.Value.Uid, fileId);
    }

    /// <summary>A version as Nextcloud lists it; null for the folder's own entry or a name that is no timestamp.</summary>
    internal static DriveItemVersion? ToVersion(string relativeHref, DavResponse response)
    {
        var id = DriveItemMapping.LastSegment(relativeHref.TrimEnd('/'));
        if (relativeHref.EndsWith('/') || !IsVersionId(id))
        {
            return null;
        }

        var modified = DateTimeOffset.TryParse(response.Text(DavNames.GetLastModified), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var m)
            ? m.ToUniversalTime()
            : (DateTimeOffset?)null;
        return new DriveItemVersion(id)
        {
            LastModifiedDateTime = modified,
            Size = long.TryParse(response.Text(DavNames.GetContentLength), NumberStyles.None, CultureInfo.InvariantCulture, out var size) ? size : 0,
            ModifiedBy = response.Text(Author),
            Label = response.Text(Label),
        };
    }

    /// <summary>Nextcloud names a version by its Unix timestamp; anything else can't be one.</summary>
    internal static bool IsVersionId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 19 && id.All(char.IsAsciiDigit);

    private async Task<Result<string>> VersionHrefAsync(DriveItemRef item, string versionId, CancellationToken cancellationToken)
    {
        if (!IsVersionId(versionId))
        {
            return Error.NotFound("The item has no such version.", "versionId");
        }

        var folder = await VersionsOfAsync(item, cancellationToken).ConfigureAwait(false);
        return folder.IsSuccess ? folder.Value.Href + versionId : folder.Error;
    }

    /// <summary>
    /// The file's versions folder, <c>versions/{uid}/versions/{fileid}/</c>, and the id of its current version (its
    /// modification time in Unix seconds; null when unknown).
    /// </summary>
    private async Task<Result<(string Href, string? CurrentId)>> VersionsOfAsync(DriveItemRef item, CancellationToken cancellationToken)
    {
        var home = await locator.HomeAsync(item, cancellationToken).ConfigureAwait(false);
        if (!home.IsSuccess)
        {
            return home.Error;
        }

        var located = await locator.LocateAsync(home.Value, item, cancellationToken).ConfigureAwait(false);
        if (!located.IsSuccess)
        {
            return located.Error;
        }

        if (located.Value.IsFolder || located.Value.FileId is not { Length: > 0 } fileId)
        {
            return DriveErrors.NotSupported("A folder has no versions.");
        }

        var modified = DateTimeOffset.TryParse(located.Value.Response.Text(DavNames.GetLastModified), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var m)
            ? m.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
            : null;
        return ($"versions/{home.Value.Uid}/versions/{fileId}/", modified);
    }
}

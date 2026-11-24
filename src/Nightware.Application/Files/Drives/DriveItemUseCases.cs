using Nightware.Domain.Files;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Files.Drives;

/// <summary>Errors shared by the drive use cases, with Graph's OneDrive codes.</summary>
public static class DriveErrors
{
    public const string NameAlreadyExistsCode = "nameAlreadyExists";
    public const string NotSupportedCode = "notSupported";

    public static Error InvalidName(string? target = "name") =>
        Error.Validation("A name can't be empty, '.' or '..', or hold '/', '\\' or control characters, and has at most 250 characters.", target);

    public static Error NotSupported(string message, string? target = null) =>
        new(ErrorKind.Validation, NotSupportedCode, message, target);

    public static Error TooLarge(long limit) =>
        new(ErrorKind.PayloadTooLarge, "requestEntityTooLarge", $"The file is larger than the {limit} bytes this operation takes.");
}

/// <summary>Graph <c>GET /me/drive</c>, <c>/drives/{id}</c>.</summary>
public sealed class GetDrive(IDriveStore store)
{
    public Task<Result<Drive>> ExecuteAsync(string? driveId, CancellationToken cancellationToken) =>
        store.GetDriveAsync(driveId, cancellationToken);
}

/// <summary>Graph <c>GET …/items/{id}</c> (and <c>root</c>, and path addressing).</summary>
public sealed class GetDriveItem(IDriveStore store)
{
    public Task<Result<DriveItem>> ExecuteAsync(DriveItemRef item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        return store.GetItemAsync(item, cancellationToken);
    }
}

/// <summary>Graph <c>GET …/children</c>.</summary>
public sealed class ListDriveItemChildren(IDriveStore store)
{
    public async Task<Result<Page<DriveItem>>> ExecuteAsync(DriveItemRef folder, DriveListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(query);
        if (query.Top is < 1 or > DriveLimits.MaxPageSize)
        {
            return Error.Validation($"$top must be between 1 and {DriveLimits.MaxPageSize}.", "$top");
        }

        return await store.ListChildrenAsync(folder, query, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// The first leg of <c>GET …/content</c>: the item exists and is a file, before the API hands out a download URL.
/// The bytes are read on the second leg (<see cref="DownloadDriveItem"/>).
/// </summary>
public sealed class PrepareDriveItemDownload(IDriveStore store)
{
    public async Task<Result<DriveItem>> ExecuteAsync(DriveItemRef item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        var found = await store.GetItemAsync(item, cancellationToken).ConfigureAwait(false);
        if (!found.IsSuccess)
        {
            return found;
        }

        return found.Value.IsFolder ? DriveErrors.NotSupported("A folder has no content to download.") : found;
    }
}

/// <summary>The second leg of <c>GET …/content</c>: the file's bytes.</summary>
public sealed class DownloadDriveItem(IDriveStore store)
{
    public Task<Result<BlobContent>> ExecuteAsync(DriveItemRef item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        return store.OpenContentAsync(item, cancellationToken);
    }
}

/// <summary>Graph's simple upload, <c>PUT …:/{name}:/content</c> or <c>PUT items/{id}/content</c>: up to 250 MB.</summary>
public sealed class UploadDriveItemContent(IDriveStore store)
{
    public async Task<Result<DriveItem>> ExecuteAsync(DriveItemRef target, DriveUpload upload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(upload);
        if (target.IsRoot)
        {
            return Error.Validation("The root folder has no content.");
        }

        if (upload.Length > DriveLimits.SimpleUploadMaxBytes)
        {
            return DriveErrors.TooLarge(DriveLimits.SimpleUploadMaxBytes);
        }

        return await store.WriteContentAsync(target, upload, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Graph <c>POST …/children</c> with a <c>folder</c> facet.</summary>
public sealed class CreateDriveFolder(IDriveStore store)
{
    public async Task<Result<DriveItem>> ExecuteAsync(DriveItemRef parent, string? name, ConflictBehavior conflict, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (!DriveItemRef.IsValidName(name))
        {
            return DriveErrors.InvalidName();
        }

        return await store.CreateFolderAsync(parent, name!, conflict, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Graph <c>PATCH …/items/{id}</c>: rename and/or move.</summary>
public sealed class UpdateDriveItem(IDriveStore store)
{
    public async Task<Result<DriveItem>> ExecuteAsync(DriveItemRef item, DriveItemChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(change);
        if (change.Name is not null && !DriveItemRef.IsValidName(change.Name))
        {
            return DriveErrors.InvalidName();
        }

        if (item.IsRoot && (change.Name is not null || change.NewParent is not null))
        {
            return Error.Validation("The root folder can't be renamed or moved.");
        }

        // Nothing the façade can change was asked for (e.g. only a description): answer the item as it is, as Graph does.
        return change.Name is null && change.NewParent is null
            ? await store.GetItemAsync(item, cancellationToken).ConfigureAwait(false)
            : await store.UpdateAsync(item, change, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Graph <c>DELETE …/items/{id}</c>.</summary>
public sealed class DeleteDriveItem(IDriveStore store)
{
    public async Task<Result> ExecuteAsync(DriveItemRef item, string? ifMatch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.IsRoot
            ? Error.Forbidden("The root folder can't be deleted.")
            : await store.DeleteAsync(item, ifMatch, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Graph <c>POST …/items/{id}/copy</c>, done before answering (ADR 0008 D4).</summary>
public sealed class CopyDriveItem(IDriveStore store)
{
    public async Task<Result<DriveItem>> ExecuteAsync(
        DriveItemRef item, DriveItemRef? parent, string? name, ConflictBehavior conflict, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (name is not null && !DriveItemRef.IsValidName(name))
        {
            return DriveErrors.InvalidName();
        }

        if (item.IsRoot)
        {
            return Error.Validation("The root folder can't be copied.");
        }

        return await store.CopyAsync(item, parent, name, conflict, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Graph <c>GET …/root/search(q='…')</c>.</summary>
public sealed class SearchDrive(IDriveStore store)
{
    public const int MaxQueryLength = 250;

    public async Task<Result<Page<DriveItem>>> ExecuteAsync(string? text, DriveListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length is 0 or > MaxQueryLength || trimmed.Any(char.IsControl))
        {
            return Error.Validation($"q must be 1 to {MaxQueryLength} characters.", "q");
        }

        if (query.Top is < 1 or > DriveLimits.MaxPageSize)
        {
            return Error.Validation($"$top must be between 1 and {DriveLimits.MaxPageSize}.", "$top");
        }

        return await store.SearchAsync(trimmed, query, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>One window of a drive search for Microsoft Search (<c>POST /search/query</c>).</summary>
/// <param name="MoreAvailable">More items match after this window.</param>
public sealed record DriveSearchHits(IReadOnlyList<DriveItem> Items, bool MoreAvailable);

/// <summary>
/// Microsoft Search's <c>driveItem</c> search (<c>POST /search/query</c>) over the drive: the items whose name contains
/// the text, <paramref name="from"/> and <paramref name="size"/> as Graph's request names them.
/// </summary>
public sealed class QueryDriveItems(SearchDrive search)
{
    /// <summary>Graph's largest <c>size</c> for <c>driveItem</c>.</summary>
    public const int MaxSize = 500;

    /// <summary>Graph's <c>size</c> when the request leaves it out.</summary>
    public const int DefaultSize = 25;

    public async Task<Result<DriveSearchHits>> ExecuteAsync(string? text, int from, int size, CancellationToken cancellationToken)
    {
        if (from < 0)
        {
            return Error.Validation("from cannot be negative.", "from");
        }

        if (size is < 1 or > MaxSize)
        {
            return Error.Validation($"size must be between 1 and {MaxSize}.", "size");
        }

        if (from + size > DriveLimits.MaxPageSize)
        {
            return Error.Validation($"from + size can't exceed {DriveLimits.MaxPageSize}.", "from");
        }

        var page = await search.ExecuteAsync(text, new DriveListQuery(from + size, null), cancellationToken).ConfigureAwait(false);
        return page.IsSuccess
            ? new DriveSearchHits(page.Value.Items.Skip(from).ToList(), page.Value.SkipToken is not null)
            : page.Error;
    }
}

/// <summary>Graph <c>GET /me/drive/recent</c>: the drive's recently modified files, newest first.</summary>
public sealed class ListRecentDriveItems(IDriveStore store)
{
    /// <summary>Graph's <c>recent</c> isn't paged; this is how many it lists.</summary>
    public const int Limit = 100;

    public Task<Result<IReadOnlyList<DriveItem>>> ExecuteAsync(CancellationToken cancellationToken) =>
        store.RecentAsync(Limit, cancellationToken);
}

/// <summary>Graph <c>GET /me/drive/sharedWithMe</c>: what other users shared with the caller.</summary>
public sealed class ListSharedWithMe(IDriveSharing sharing)
{
    public Task<Result<IReadOnlyList<DriveItem>>> ExecuteAsync(CancellationToken cancellationToken) =>
        sharing.SharedWithMeAsync(cancellationToken);
}

/// <summary>Graph <c>GET /me/drive/special/{name}</c>.</summary>
public sealed class GetSpecialFolder(IDriveStore store)
{
    public async Task<Result<DriveItem>> ExecuteAsync(string? name, string? appName, CancellationToken cancellationToken)
    {
        SpecialFolder? folder = name?.ToLowerInvariant() switch
        {
            "documents" => SpecialFolder.Documents,
            "photos" => SpecialFolder.Photos,
            "cameraroll" => SpecialFolder.CameraRoll,
            "music" => SpecialFolder.Music,
            "approot" => SpecialFolder.AppRoot,
            _ => null,
        };
        if (folder is not { } special)
        {
            return Error.NotFound($"'{name}' is not a special folder this drive has.");
        }

        if (special == SpecialFolder.AppRoot && !DriveItemRef.IsValidName(appName))
        {
            return Error.Validation("The app folder needs the calling application's name, and the access token names none.");
        }

        return await store.GetSpecialFolderAsync(special, appName, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Graph <c>GET …/root/delta</c>.</summary>
public sealed class GetDriveDelta(IDriveDelta delta)
{
    public async Task<Result<DeltaPage<DriveItem>>> ExecuteAsync(DeltaToken? token, int? pageSize, bool latest, CancellationToken cancellationToken)
    {
        var size = pageSize ?? DriveLimits.DefaultPageSize;
        if (size is < 1 or > DriveLimits.MaxPageSize)
        {
            return Error.Validation($"$top must be between 1 and {DriveLimits.MaxPageSize}.", "$top");
        }

        return await delta.ChangesAsync(token, size, latest, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Graph <c>GET …/thumbnails/{set}/{size}/content</c> (and the item behind <c>…/thumbnails</c>).</summary>
public sealed class OpenDriveThumbnail(IDriveStore store)
{
    public Task<Result<BlobContent>> ExecuteAsync(DriveItemRef item, ThumbnailSize size, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        return store.OpenThumbnailAsync(item, size, cancellationToken);
    }
}

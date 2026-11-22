using Nightware.Domain.Files;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Files.Drives;

/// <summary>Graph's <c>$orderby</c> on a drive listing: <c>name</c>, <c>lastModifiedDateTime</c> or <c>size</c>.</summary>
public enum DriveSortKey
{
    Name,
    LastModifiedDateTime,
    Size,
}

/// <summary>One page of a drive listing (children, search). <paramref name="SkipToken"/> is the store's own continuation.</summary>
public sealed record DriveListQuery(int Top, string? SkipToken, DriveSortKey Sort = DriveSortKey.Name, bool Descending = false);

/// <summary>New content for a file. <paramref name="Length"/> is checked against what arrives when known.</summary>
public sealed record DriveUpload(Stream Content, string ContentType, long? Length, ConflictBehavior Conflict)
{
    /// <summary>Replace only while the item's eTag is still this one (<c>If-Match</c>).</summary>
    public string? IfMatch { get; init; }
}

/// <summary>A <c>PATCH</c> of an item: a new name, a new parent, or both.</summary>
public sealed record DriveItemChange(string? Name, DriveItemRef? NewParent, ConflictBehavior Conflict)
{
    public string? IfMatch { get; init; }
}

/// <summary>
/// The caller's drive over the file backend (ADR 0008), as the caller. A missing item is <see cref="ErrorKind.NotFound"/>;
/// a taken name under <see cref="ConflictBehavior.Fail"/> is <see cref="ErrorKind.Conflict"/> with code
/// <c>nameAlreadyExists</c>; a drive other than the caller's (<see cref="DriveItemRef.DriveId"/>) is NotFound.
/// </summary>
public interface IDriveStore
{
    /// <param name="driveId">The drive the route named; null for the caller's own.</param>
    Task<Result<Drive>> GetDriveAsync(string? driveId, CancellationToken cancellationToken);

    Task<Result<DriveItem>> GetItemAsync(DriveItemRef item, CancellationToken cancellationToken);

    Task<Result<Page<DriveItem>>> ListChildrenAsync(DriveItemRef folder, DriveListQuery query, CancellationToken cancellationToken);

    /// <summary>A file's bytes. The returned stream holds a backend response open: open and dispose it.</summary>
    Task<Result<BlobContent>> OpenContentAsync(DriveItemRef item, CancellationToken cancellationToken);

    /// <summary>Writes a file's content: creates it when <paramref name="target"/> names a free path, otherwise replaces it.</summary>
    Task<Result<DriveItem>> WriteContentAsync(DriveItemRef target, DriveUpload upload, CancellationToken cancellationToken);

    Task<Result<DriveItem>> CreateFolderAsync(DriveItemRef parent, string name, ConflictBehavior conflict, CancellationToken cancellationToken);

    Task<Result<DriveItem>> UpdateAsync(DriveItemRef item, DriveItemChange change, CancellationToken cancellationToken);

    /// <summary>Deletes an item (to the backend's trash bin); the root can't be deleted.</summary>
    Task<Result> DeleteAsync(DriveItemRef item, string? ifMatch, CancellationToken cancellationToken);

    /// <summary>Copies an item (a folder with everything in it) into <paramref name="parent"/>, or beside itself when null.</summary>
    Task<Result<DriveItem>> CopyAsync(DriveItemRef item, DriveItemRef? parent, string? name, ConflictBehavior conflict, CancellationToken cancellationToken);

    /// <summary>Items anywhere in the drive whose name contains <paramref name="text"/>.</summary>
    Task<Result<Page<DriveItem>>> SearchAsync(string text, DriveListQuery query, CancellationToken cancellationToken);

    /// <summary>A special folder, created on first use as Graph creates them.</summary>
    /// <param name="appName">For <see cref="SpecialFolder.AppRoot"/>: the calling application's name.</param>
    Task<Result<DriveItem>> GetSpecialFolderAsync(SpecialFolder folder, string? appName, CancellationToken cancellationToken);

    /// <summary>A rendered preview, at most <paramref name="size"/>'s edge on each side. An item without one is NotFound.</summary>
    Task<Result<BlobContent>> OpenThumbnailAsync(DriveItemRef item, ThumbnailSize size, CancellationToken cancellationToken);
}

/// <summary>Changes in the caller's drive since a token (ADR 0008 D5).</summary>
public interface IDriveDelta
{
    /// <param name="token">Null for an initial listing of every item.</param>
    /// <param name="latest">Graph's <c>token=latest</c>: no items, only a token for changes from now on.</param>
    Task<Result<DeltaPage<DriveItem>>> ChangesAsync(DeltaToken? token, int pageSize, bool latest, CancellationToken cancellationToken);
}

/// <summary>
/// The file an upload session is for: a name under a parent folder, its size (0 when the client didn't say; the chunks'
/// <c>Content-Range</c> total then decides) and conflict behaviour.
/// </summary>
public sealed record DriveUploadFile(DriveItemRef Parent, string Name, long Size, ConflictBehavior Conflict);

/// <summary>A session's state: its id (also its token), its expiry and the next byte it wants.</summary>
public sealed record DriveUploadState(string SessionId, DateTimeOffset ExpiresAt, long NextExpected);

/// <summary>After a chunk: the next byte wanted, or the item the last chunk completed.</summary>
public sealed record DriveUploadProgress(long NextExpected, DriveItem? Completed);

/// <summary>Large files in chunks (<c>createUploadSession</c>), staged in the backend, with no state in the façade.</summary>
public interface IDriveUploads
{
    Task<Result<DriveUploadState>> BeginAsync(DriveUploadFile file, CancellationToken cancellationToken);

    /// <summary>Stores <paramref name="length"/> bytes from <paramref name="first"/>, which must be the next byte wanted.</summary>
    Task<Result<DriveUploadProgress>> AppendAsync(string sessionId, long first, long length, long total, Stream chunk, CancellationToken cancellationToken);

    Task<Result<DriveUploadState>> GetAsync(string sessionId, CancellationToken cancellationToken);

    Task<Result> CancelAsync(string sessionId, CancellationToken cancellationToken);
}

/// <summary>Sharing links and invitations on the caller's items (ADR 0008 D6).</summary>
public interface IDriveSharing
{
    Task<Result<DriveShare>> CreateLinkAsync(DriveItemRef item, SharingLinkRequest request, CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<DriveShare>>> InviteAsync(DriveItemRef item, DriveInvitation invitation, CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<DriveShare>>> ListPermissionsAsync(DriveItemRef item, CancellationToken cancellationToken);

    /// <summary>Removes one of the item's own permissions; an inherited one is <see cref="ErrorKind.Forbidden"/>.</summary>
    Task<Result> DeletePermissionAsync(DriveItemRef item, string permissionId, CancellationToken cancellationToken);
}

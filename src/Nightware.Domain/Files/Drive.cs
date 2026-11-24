using Nightware.Domain.Kernel;

namespace Nightware.Domain.Files;

/// <summary>The caller's personal drive (Graph <c>drive</c>, ADR 0008 D1): their Nextcloud home.</summary>
/// <param name="Id">Opaque and stable: the same user always has the same drive id.</param>
public sealed record Drive(string Id, string Name)
{
    public const string PersonalDriveType = "personal";

    public string? OwnerId { get; init; }

    public string? OwnerDisplayName { get; init; }

    public string? WebUrl { get; init; }

    public DateTimeOffset? LastModifiedDateTime { get; init; }

    public DriveQuota? Quota { get; init; }
}

/// <summary>
/// Storage use. <see cref="Total"/> and <see cref="Remaining"/> are null when the backend reports no limit
/// (Graph then leaves them out rather than inventing one).
/// </summary>
public sealed record DriveQuota(long Used, long? Remaining, long? Total)
{
    /// <summary>Graph's <c>quota.state</c>: <c>normal</c>, <c>nearing</c> (under 10% left), <c>critical</c> (under 1%), <c>exceeded</c>.</summary>
    public string State => (Remaining, Total) switch
    {
        ({ } left, _) when left <= 0 => "exceeded",
        ({ } left, { } total) when total > 0 && left < total / 100 => "critical",
        ({ } left, { } total) when total > 0 && left < total / 10 => "nearing",
        _ => "normal",
    };
}

/// <summary>A file or folder in the caller's drive (Graph <c>driveItem</c>).</summary>
/// <param name="Id">The backend's stable file id; it survives renames and moves.</param>
public sealed record DriveItem(string Id, string Name)
{
    /// <summary>The drive the item is in (Graph <c>parentReference.driveId</c>).</summary>
    public string? DriveId { get; init; }

    public long Size { get; init; }

    public bool IsFolder { get; init; }

    /// <summary>The drive's root folder (Graph's <c>root</c> facet; it has no parent).</summary>
    public bool IsRoot { get; init; }

    public string? MimeType { get; init; }

    /// <summary>Changes with the item's metadata or content (Graph <c>eTag</c>).</summary>
    public string? ETag { get; init; }

    /// <summary>Changes with the item's content only (Graph <c>cTag</c>); null where the backend can't tell.</summary>
    public string? CTag { get; init; }

    public DateTimeOffset? CreatedDateTime { get; init; }

    public DateTimeOffset? LastModifiedDateTime { get; init; }

    /// <summary>The parent folder's id; null for the root.</summary>
    public string? ParentId { get; init; }

    /// <summary>The parent folder's path below the root, <c>/</c> for an item in the root; null for the root itself.</summary>
    public string? ParentPath { get; init; }

    /// <summary>For a folder, how many items it holds directly.</summary>
    public int? ChildCount { get; init; }

    public string? WebUrl { get; init; }

    /// <summary>
    /// The content's SHA-1 as upper-case hex (Graph <c>file.hashes.sha1Hash</c>), when the backend stored one (an uploader
    /// sent it); never computed by reading the content.
    /// </summary>
    public string? Sha1Hash { get; init; }

    /// <summary>The content's SHA-256 as upper-case hex (Graph <c>file.hashes.sha256Hash</c>), as <see cref="Sha1Hash"/>.</summary>
    public string? Sha256Hash { get; init; }

    /// <summary>The backend can render a preview, so the item has a thumbnail set.</summary>
    public bool HasThumbnail { get; init; }

    /// <summary>Shared with someone, or shared with the caller by someone else.</summary>
    public bool IsShared { get; init; }

    /// <summary>
    /// For an item someone else shared with the caller (<c>sharedWithMe</c>): who owns it and when they shared it
    /// (Graph <c>remoteItem.shared</c>); null otherwise.
    /// </summary>
    public DriveItemSharer? SharedBy { get; init; }

    /// <summary>A delta entry for an item that no longer exists (Graph's <c>deleted</c> facet); only <see cref="Id"/> is known.</summary>
    public bool IsDeleted { get; init; }

    public static DriveItem Deleted(string id) => new(id, string.Empty) { IsDeleted = true };
}

/// <summary>Who shared an item with the caller, and when.</summary>
public sealed record DriveItemSharer(string Id, string? DisplayName, DateTimeOffset? SharedDateTime);

/// <summary>Where an item is: an anchor (an item id, or the root when null) and, optionally, a path below it.</summary>
/// <param name="ItemId">The anchor item's id; null for the drive's root.</param>
/// <param name="Path">Segments below the anchor (Graph's <c>:/a/b:</c> addressing), already validated.</param>
public sealed record DriveItemRef(string? ItemId, IReadOnlyList<string> Path)
{
    public const int MaxPathSegments = 64;
    public const int MaxSegmentLength = 250;

    public static DriveItemRef Root { get; } = new(null, []);

    /// <summary>
    /// The drive the route named (<c>/drives/{driveId}/…</c>); null for <c>/me/drive</c>. The store refuses any drive
    /// but the caller's own.
    /// </summary>
    public string? DriveId { get; init; }

    public static DriveItemRef ById(string itemId) => new(itemId, []);

    public bool IsRoot => ItemId is null && Path.Count == 0;

    /// <summary>The same anchor with one more segment (a child named <paramref name="name"/>).</summary>
    public DriveItemRef Child(string name) => this with { Path = [.. Path, name] };

    /// <summary>
    /// Splits a path below an anchor (<c>a/b/c.txt</c>, leading and trailing slashes allowed) into checked segments.
    /// An empty, <c>.</c> or <c>..</c> segment, one with a backslash or control character, or one that is too long is
    /// refused: such a path could name something other than what it says once a backend decodes it.
    /// </summary>
    public static bool TryParsePath(string? path, out IReadOnlyList<string> segments)
    {
        segments = [];
        var trimmed = (path ?? string.Empty).Trim('/');
        if (trimmed.Length == 0)
        {
            return true;
        }

        var parts = trimmed.Split('/');
        if (parts.Length > MaxPathSegments || !parts.All(IsValidName))
        {
            return false;
        }

        segments = parts;
        return true;
    }

    /// <summary>A name a file or folder may have: not empty, not <c>.</c>/<c>..</c>, no separators or control characters.</summary>
    public static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name)
        && name.Length <= MaxSegmentLength
        && name is not ("." or "..")
        && !name.Any(static c => c is '/' or '\\' || char.IsControl(c));
}

/// <summary>Graph's <c>@microsoft.graph.conflictBehavior</c>: what to do when the target name is taken.</summary>
public enum ConflictBehavior
{
    Fail,
    Replace,
    Rename,
}

/// <summary>The drive's limits (ADR 0008 D3).</summary>
public static class DriveLimits
{
    /// <summary>Graph's simple upload limit.</summary>
    public const long SimpleUploadMaxBytes = 250L * 1024 * 1024;

    /// <summary>The largest file an upload session takes.</summary>
    public const long UploadSessionMaxBytes = 10L * 1024 * 1024 * 1024;

    /// <summary>Graph's largest chunk ("less than 60 MiB").</summary>
    public const long MaxChunkBytes = 60L * 1024 * 1024;

    /// <summary>Most items in one page of children, search results or delta.</summary>
    public const int MaxPageSize = 1000;

    public const int DefaultPageSize = 200;
}

/// <summary>Graph's special folders (<c>drive/special/{name}</c>) the drive offers.</summary>
public enum SpecialFolder
{
    Documents,
    Photos,
    CameraRoll,
    Music,
    AppRoot,
}

/// <summary>A sharing link or invitation on an item (Graph <c>permission</c>).</summary>
/// <param name="Id">The backend's share id.</param>
/// <param name="Roles"><c>read</c> or <c>write</c> (and <c>owner</c> for the item's owner).</param>
public sealed record DriveShare(string Id, IReadOnlyList<string> Roles)
{
    /// <summary>For a link: its URL and type (<c>view</c>/<c>edit</c>); null for an invitation.</summary>
    public SharingLink? Link { get; init; }

    /// <summary>For an invitation: who it was given to.</summary>
    public EmailAddress? GrantedTo { get; init; }

    public DateTimeOffset? ExpirationDateTime { get; init; }

    public bool HasPassword { get; init; }

    /// <summary>The permission comes from an ancestor folder (Graph <c>inheritedFrom</c>), so it can't be removed here.</summary>
    public string? InheritedFromId { get; init; }
}

/// <param name="Type"><c>view</c>, <c>edit</c> or <c>blocksDownload</c> (view only, no download).</param>
/// <param name="Scope">Always <c>anonymous</c>: anyone with the link.</param>
public sealed record SharingLink(string Type, string Scope, string WebUrl)
{
    /// <summary>The link opens the item for viewing but doesn't offer it for download (Graph <c>preventsDownload</c>).</summary>
    public bool PreventsDownload => Type == SharingLinkTypes.BlocksDownload;
}

/// <summary>The <c>createLink</c> types the drive offers.</summary>
public static class SharingLinkTypes
{
    public const string View = "view";
    public const string Edit = "edit";
    public const string BlocksDownload = "blocksDownload";
}

/// <summary>What <c>createLink</c> asks for.</summary>
public sealed record SharingLinkRequest(string Type, DateTimeOffset? ExpirationDateTime, string? Password);

/// <summary>What <c>invite</c> asks for: directory users by address, each given <paramref name="Roles"/>.</summary>
public sealed record DriveInvitation(IReadOnlyList<string> Recipients, IReadOnlyList<string> Roles, DateTimeOffset? ExpirationDateTime);

/// <summary>Graph's three default thumbnail sizes (ADR 0008 D6).</summary>
public enum ThumbnailSize
{
    Small,
    Medium,
    Large,
}

/// <summary>A thumbnail size's edge in pixels.</summary>
public static class ThumbnailSizes
{
    public static int EdgeOf(ThumbnailSize size) => size switch
    {
        ThumbnailSize.Small => 96,
        ThumbnailSize.Medium => 176,
        _ => 800,
    };
}

using System.Text.Json.Serialization;

using Nightware.Api.Features.Tasks.Dto;
using Nightware.Domain.Files;

namespace Nightware.Api.Features.Files.Drives.Dto;

/// <summary>Graph <c>drive</c> (ADR 0008 D1).</summary>
public sealed record DriveDto
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string DriveType { get; init; } = Drive.PersonalDriveType;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WebUrl { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? LastModifiedDateTime { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IdentitySetDto? Owner { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public QuotaDto? Quota { get; init; }

    public static DriveDto From(Drive drive, string? filesBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(drive);
        return new DriveDto
        {
            Id = drive.Id,
            Name = drive.Name,
            WebUrl = filesBaseUrl is null ? null : $"{filesBaseUrl.TrimEnd('/')}/apps/files/",
            LastModifiedDateTime = drive.LastModifiedDateTime,
            Owner = drive.OwnerId is null ? null : new IdentitySetDto { User = new IdentityDto { Id = drive.OwnerId, DisplayName = drive.OwnerDisplayName } },
            Quota = drive.Quota is { } q ? new QuotaDto { Used = q.Used, Remaining = q.Remaining, Total = q.Total, Deleted = 0, State = q.State } : null,
        };
    }
}

public sealed record QuotaDto
{
    public long Used { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Remaining { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Total { get; init; }

    public long Deleted { get; init; }

    public required string State { get; init; }
}

public sealed record IdentitySetDto
{
    public required IdentityDto User { get; init; }
}

public sealed record IdentityDto
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DisplayName { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Email { get; init; }
}

/// <summary>
/// Graph <c>driveItem</c> of the caller's drive. A file has the <c>file</c> facet, a folder the <c>folder</c> facet and the
/// root also the <c>root</c> facet. A delta entry for a removed item is only <c>id</c> and <c>deleted</c>. No
/// <c>@microsoft.graph.downloadUrl</c>: <c>…/content</c> redirects to one (ADR 0008 D3). Nextcloud has no
/// <c>quickXorHash</c>, so <c>file.hashes</c> is not emitted.
/// </summary>
public sealed record DriveItemResourceDto
{
    public required string Id { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Size { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ETag { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CTag { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(PreciseInstantConverter))]
    public DateTimeOffset? CreatedDateTime { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(PreciseInstantConverter))]
    public DateTimeOffset? LastModifiedDateTime { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WebUrl { get; init; }

    /// <summary>
    /// A short-lived, self-authorising URL for the content (Graph's pre-authenticated download URL); only on a single
    /// file's GET, as each carries a sealed copy of the caller's token (ADR 0008 D3).
    /// </summary>
    [JsonPropertyName("@microsoft.graph.downloadUrl")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DownloadUrl { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ItemReferenceDto? ParentReference { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DriveFileFacetDto? File { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DriveFolderFacetDto? Folder { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public EmptyFacetDto? Root { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FileSystemInfoDto? FileSystemInfo { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SharedFacetDto? Shared { get; init; }

    /// <summary>Graph's <c>publication</c> facet, only while the file is checked out (<c>level: checkout</c>).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PublicationFacetDto? Publication { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DeletedFacetDto? Deleted { get; init; }

    /// <summary>
    /// On <c>sharedWithMe</c> and <c>recent</c> entries: the item itself (Graph's pointer to an item that may live in
    /// another drive; here always the caller's).
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RemoteItemDto? RemoteItem { get; init; }

    /// <summary>The folder's children, when asked for with <c>$expand=children</c>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<DriveItemResourceDto>? Children { get; init; }

    /// <param name="filesBaseUrl">The public Nextcloud URL (<c>Discovery:FilesBaseUrls</c>' first), for <c>webUrl</c>; null leaves it out.</param>
    public static DriveItemResourceDto From(DriveItem item, string? filesBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.IsDeleted)
        {
            return new DriveItemResourceDto { Id = item.Id, Deleted = new DeletedFacetDto() };
        }

        return new DriveItemResourceDto
        {
            Id = item.Id,
            Name = item.Name,
            Size = item.Size,
            ETag = item.ETag is null ? null : $"\"{item.ETag}\"",
            CTag = item.CTag is null ? null : $"\"c:{item.CTag}\"",
            CreatedDateTime = item.CreatedDateTime,
            LastModifiedDateTime = item.LastModifiedDateTime,
            WebUrl = filesBaseUrl is null || item.Id.Length == 0 ? null : $"{filesBaseUrl.TrimEnd('/')}/f/{item.Id}",
            ParentReference = item.ParentId is null
                ? (item.DriveId is null ? null : new ItemReferenceDto { DriveId = item.DriveId })
                : new ItemReferenceDto
                {
                    DriveId = item.DriveId,
                    Id = item.ParentId,
                    Path = item.ParentPath is null or "/" ? "/drive/root:" : $"/drive/root:{item.ParentPath}",
                },
            File = item.IsFolder ? null : new DriveFileFacetDto
            {
                MimeType = item.MimeType ?? "application/octet-stream",
                Hashes = item.Sha1Hash is null && item.Sha256Hash is null ? null : new DriveHashesDto { Sha1Hash = item.Sha1Hash, Sha256Hash = item.Sha256Hash },
            },
            Folder = item.IsFolder ? new DriveFolderFacetDto { ChildCount = item.ChildCount ?? 0 } : null,
            Root = item.IsRoot ? new EmptyFacetDto() : null,
            FileSystemInfo = new FileSystemInfoDto { CreatedDateTime = item.CreatedDateTime, LastModifiedDateTime = item.LastModifiedDateTime },
            Shared = item.IsShared ? new SharedFacetDto() : null,
            Publication = item.CheckedOutBy is { } by
                ? new PublicationFacetDto { CheckedOutBy = new IdentitySetDto { User = new IdentityDto { Id = by.Id, DisplayName = by.DisplayName } } }
                : null,
        };
    }
}

/// <summary>Graph <c>remoteItem</c>: the item an entry points at.</summary>
public sealed record RemoteItemDto
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public long Size { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WebUrl { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(PreciseInstantConverter))]
    public DateTimeOffset? CreatedDateTime { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(PreciseInstantConverter))]
    public DateTimeOffset? LastModifiedDateTime { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ItemReferenceDto? ParentReference { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DriveFileFacetDto? File { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DriveFolderFacetDto? Folder { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RemoteSharedDto? Shared { get; init; }

    /// <summary>An entry of <c>sharedWithMe</c> or <c>recent</c>: the item, and a <c>remoteItem</c> pointing at it.</summary>
    public static DriveItemResourceDto Entry(DriveItem item, string? filesBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(item);
        var dto = DriveItemResourceDto.From(item, filesBaseUrl);
        return dto with
        {
            RemoteItem = new RemoteItemDto
            {
                Id = item.Id,
                Name = item.Name,
                Size = item.Size,
                WebUrl = dto.WebUrl,
                CreatedDateTime = item.CreatedDateTime,
                LastModifiedDateTime = item.LastModifiedDateTime,
                ParentReference = new ItemReferenceDto { DriveId = item.DriveId, Id = item.ParentId },
                File = dto.File,
                Folder = dto.Folder,
                Shared = item.SharedBy is { } by
                    ? new RemoteSharedDto
                    {
                        SharedDateTime = by.SharedDateTime,
                        Owner = new IdentitySetDto { User = new IdentityDto { Id = by.Id, DisplayName = by.DisplayName } },
                    }
                    : null,
            },
        };
    }
}

public sealed record RemoteSharedDto
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IdentitySetDto? Owner { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(PreciseInstantConverter))]
    public DateTimeOffset? SharedDateTime { get; init; }
}

public sealed record ItemReferenceDto
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DriveId { get; init; }

    public string DriveType { get; init; } = Drive.PersonalDriveType;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Path { get; init; }
}

public sealed record DriveFileFacetDto
{
    public required string MimeType { get; init; }

    /// <summary>Only what the backend stored; absent when it has none (it never hashes content on read).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DriveHashesDto? Hashes { get; init; }
}

public sealed record DriveHashesDto
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sha1Hash { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Sha256Hash { get; init; }
}

public sealed record DriveFolderFacetDto
{
    public int ChildCount { get; init; }
}

/// <summary>A facet whose presence is the information (<c>root</c>).</summary>
public sealed record EmptyFacetDto;

public sealed record SharedFacetDto
{
    public string Scope { get; init; } = "users";
}

public sealed record PublicationFacetDto
{
    public string Level { get; init; } = "checkout";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IdentitySetDto? CheckedOutBy { get; init; }
}

public sealed record DeletedFacetDto
{
    public string State { get; init; } = "deleted";
}

public sealed record FileSystemInfoDto
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(PreciseInstantConverter))]
    public DateTimeOffset? CreatedDateTime { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(PreciseInstantConverter))]
    public DateTimeOffset? LastModifiedDateTime { get; init; }
}

/// <summary>Graph <c>permission</c> on a driveItem.</summary>
public sealed record PermissionDto
{
    public required string Id { get; init; }

    public required IReadOnlyList<string> Roles { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SharingLinkDto? Link { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IdentitySetDto? GrantedToV2 { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(PreciseInstantConverter))]
    public DateTimeOffset? ExpirationDateTime { get; init; }

    public bool HasPassword { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ItemReferenceDto? InheritedFrom { get; init; }

    public static PermissionDto From(DriveShare share, string? driveId)
    {
        ArgumentNullException.ThrowIfNull(share);
        return new PermissionDto
        {
            Id = share.Id,
            Roles = share.Roles,
            Link = share.Link is { } link ? new SharingLinkDto { Type = link.Type, Scope = link.Scope, WebUrl = link.WebUrl, PreventsDownload = link.PreventsDownload } : null,
            GrantedToV2 = share.GrantedTo is { } who
                ? new IdentitySetDto { User = new IdentityDto { Email = who.Address, DisplayName = who.Name } }
                : null,
            ExpirationDateTime = share.ExpirationDateTime,
            HasPassword = share.HasPassword,
            InheritedFrom = share.InheritedFromId is { } from ? new ItemReferenceDto { DriveId = driveId, Id = from } : null,
        };
    }
}

public sealed record SharingLinkDto
{
    public required string Type { get; init; }

    public required string Scope { get; init; }

    public required string WebUrl { get; init; }

    public bool PreventsDownload { get; init; }
}

/// <summary>Graph <c>thumbnailSet</c>: one set, <c>id</c> <c>0</c>, with self-authorising URLs.</summary>
public sealed record ThumbnailSetDto
{
    public string Id { get; init; } = "0";

    public required ThumbnailDto Small { get; init; }

    public required ThumbnailDto Medium { get; init; }

    public required ThumbnailDto Large { get; init; }
}

/// <summary>Graph <c>thumbnail</c>: the longest edge (the backend keeps the aspect ratio inside it) and the URL.</summary>
public sealed record ThumbnailDto
{
    public int Height { get; init; }

    public int Width { get; init; }

    public required string Url { get; init; }
}

/// <summary>Graph <c>uploadSession</c> for a drive upload.</summary>
public sealed record DriveUploadSessionDto
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UploadUrl { get; init; }

    [JsonConverter(typeof(PreciseInstantConverter))]
    public DateTimeOffset? ExpirationDateTime { get; init; }

    public IReadOnlyList<string> NextExpectedRanges { get; init; } = ["0-"];
}

/// <summary>Graph's async operation status, as a copy monitor reports it.</summary>
public sealed record AsyncOperationStatusDto
{
    public string Operation { get; init; } = "itemCopy";

    public double PercentageComplete { get; init; } = 100;

    public required string ResourceId { get; init; }

    public string Status { get; init; } = "completed";
}

/// <summary>Graph <c>driveItemVersion</c>.</summary>
public sealed record DriveItemVersionDto
{
    public required string Id { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonConverter(typeof(PreciseInstantConverter))]
    public DateTimeOffset? LastModifiedDateTime { get; init; }

    public long Size { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IdentitySetDto? LastModifiedBy { get; init; }

    public static DriveItemVersionDto From(DriveItemVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);
        return new DriveItemVersionDto
        {
            Id = version.Id,
            LastModifiedDateTime = version.LastModifiedDateTime,
            Size = version.Size,
            LastModifiedBy = version.ModifiedBy is { } by ? new IdentitySetDto { User = new IdentityDto { Id = by } } : null,
        };
    }
}

/// <summary>Graph <c>itemPreviewInfo</c>: only <c>getUrl</c> (no form to post).</summary>
public sealed record ItemPreviewInfoDto
{
    public required string GetUrl { get; init; }

    public string? PostUrl { get; init; }

    public string? PostParameters { get; init; }
}

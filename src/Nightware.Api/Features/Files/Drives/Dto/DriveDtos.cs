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

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DeletedFacetDto? Deleted { get; init; }

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
            File = item.IsFolder ? null : new DriveFileFacetDto { MimeType = item.MimeType ?? "application/octet-stream" },
            Folder = item.IsFolder ? new DriveFolderFacetDto { ChildCount = item.ChildCount ?? 0 } : null,
            Root = item.IsRoot ? new EmptyFacetDto() : null,
            FileSystemInfo = new FileSystemInfoDto { CreatedDateTime = item.CreatedDateTime, LastModifiedDateTime = item.LastModifiedDateTime },
            Shared = item.IsShared ? new SharedFacetDto() : null,
        };
    }
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
            Link = share.Link is { } link ? new SharingLinkDto { Type = link.Type, Scope = link.Scope, WebUrl = link.WebUrl } : null,
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

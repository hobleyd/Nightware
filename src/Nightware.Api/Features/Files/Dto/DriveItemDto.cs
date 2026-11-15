using System.Text.Json.Serialization;

using Nightware.Domain.Files;

namespace Nightware.Api.Features.Files.Dto;

/// <summary>
/// Graph <c>driveItem</c>, trimmed to what NightMail reads (<c>name</c>, <c>size</c>, <c>file.mimeType</c>,
/// presence of <c>folder</c>) plus <c>webUrl</c> and <c>lastModifiedDateTime</c>. A file has the <c>file</c>
/// facet, a folder the <c>folder</c> facet, never both. Nextcloud has no <c>quickXorHash</c>, so
/// <c>file.hashes</c> is not emitted.
/// </summary>
public sealed record DriveItemDto
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public long Size { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FileFacetDto? File { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FolderFacetDto? Folder { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WebUrl { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? LastModifiedDateTime { get; init; }

    public static DriveItemDto From(SharedItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return new DriveItemDto
        {
            Id = item.Id,
            Name = item.Name,
            Size = item.Size,
            File = item.IsFolder ? null : new FileFacetDto { MimeType = item.MimeType ?? "application/octet-stream" },
            Folder = item.IsFolder ? new FolderFacetDto() : null,
            WebUrl = item.WebUrl,
            LastModifiedDateTime = item.LastModifiedDateTime,
        };
    }
}

/// <summary>Graph <c>file</c> facet.</summary>
public sealed record FileFacetDto
{
    public required string MimeType { get; init; }
}

/// <summary>Graph <c>folder</c> facet. Its <c>childCount</c> is not known from a share and not read by the client.</summary>
public sealed record FolderFacetDto;

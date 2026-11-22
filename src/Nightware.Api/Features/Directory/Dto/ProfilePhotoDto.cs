using System.Globalization;
using System.Text.Json.Serialization;
using Nightware.Application.Directory;

namespace Nightware.Api.Features.Directory.Dto;

/// <summary>Graph <c>profilePhoto</c>: the metadata <c>GET …/photo</c> answers (profilephoto-get).</summary>
public sealed record ProfilePhotoDto
{
    [JsonPropertyName("@odata.mediaContentType")]
    public required string MediaContentType { get; init; }

    [JsonPropertyName("@odata.mediaEtag")]
    public required string MediaEtag { get; init; }

    /// <summary>The size as Graph names it, e.g. <c>240x240</c>.</summary>
    public required string Id { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public static ProfilePhotoDto From(UserPhotoInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return new ProfilePhotoDto
        {
            MediaContentType = info.ContentType,
            MediaEtag = info.Etag,
            Id = string.Create(CultureInfo.InvariantCulture, $"{info.Width}x{info.Height}"),
            Width = info.Width,
            Height = info.Height,
        };
    }
}

using System.Security.Cryptography;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Directory;

/// <summary>A photo's metadata (Graph <c>profilePhoto</c>): its type, size and a content tag.</summary>
/// <param name="Etag">Changes whenever the image does (a hash of it); empty for the placeholder.</param>
public sealed record UserPhotoInfo(string ContentType, int Width, int Height, string Etag)
{
    /// <summary>What Graph answers for a user with no photo: a 1×1 GIF placeholder, not a 404 (profilephoto-get).</summary>
    public static UserPhotoInfo None { get; } = new("image/gif", 1, 1, string.Empty);
}

/// <summary>
/// <c>GET /me/photo</c>, <c>GET /users/{id|upn}/photo</c>: the photo's metadata, read from the image itself. A user with
/// no photo gets <see cref="UserPhotoInfo.None"/>, as Graph documents; only <c>…/photo/$value</c> is a 404.
/// </summary>
public sealed class GetUserPhotoInfo(GetSignedInUserPhoto mine, GetUserPhoto theirs)
{
    /// <param name="idOrUpn">Null for <c>/me</c>.</param>
    public async Task<Result<UserPhotoInfo>> ExecuteAsync(string? idOrUpn, CancellationToken cancellationToken)
    {
        var photo = idOrUpn is null
            ? await mine.ExecuteAsync(cancellationToken).ConfigureAwait(false)
            : await theirs.ExecuteAsync(idOrUpn, cancellationToken).ConfigureAwait(false);
        if (!photo.IsSuccess)
        {
            return UserPhotos.IsNone(photo.Error) ? UserPhotoInfo.None : photo.Error;
        }

        byte[] bytes;
        var stream = await photo.Value.Open(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            bytes = buffer.ToArray();
        }

        var info = ImageInfo.Read(bytes);
        var etag = $"\"{Convert.ToHexString(SHA256.HashData(bytes))[..8]}\"";
        return info is null
            ? new UserPhotoInfo(photo.Value.ContentType, 0, 0, etag)
            : new UserPhotoInfo(info.MediaType, info.Width, info.Height, etag);
    }
}

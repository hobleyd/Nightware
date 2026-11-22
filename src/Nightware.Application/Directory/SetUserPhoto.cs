using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Directory;

/// <summary>
/// <c>PUT|PATCH /me/photo/$value</c> (and <c>/users/{caller}/photo/$value</c>): sets the signed-in user's photo.
/// </summary>
/// <remarks>
/// The photo store (Nextcloud) keeps only a square JPEG or PNG; anything else it answers "not square" and stores nothing,
/// and the façade has no image library to crop with. So those are a 400 that says why, rather than a 200 that changed
/// nothing. The image's own bytes decide its type, not the request's <c>Content-Type</c> (Graph requires one, and
/// clients send <c>image/jpeg</c> for PNGs too).
/// </remarks>
public sealed class SetUserPhoto(IMailboxResolver resolver, IUserPhotoWriter photos)
{
    /// <param name="idOrUpn">Null for <c>/me</c>.</param>
    public async Task<Result> ExecuteAsync(string? idOrUpn, ReadOnlyMemory<byte> image, CancellationToken cancellationToken)
    {
        if (image.Length > UserPhotos.MaxUploadBytes)
        {
            return new Error(ErrorKind.PayloadTooLarge, "requestEntityTooLarge", "The photo is larger than 4 MB.");
        }

        var info = ImageInfo.Read(image.Span);
        if (info is null)
        {
            return Error.Validation("The photo must be a JPEG or PNG image.");
        }

        if (!info.IsSquare)
        {
            return Error.Validation($"The photo must be square (this one is {info.Width}x{info.Height}); crop it first.");
        }

        var owner = await PhotoOwner.SelfAsync(resolver, idOrUpn, cancellationToken).ConfigureAwait(false);
        return owner.IsSuccess
            ? await photos.SetAsync(owner.Value, image, info.MediaType, cancellationToken).ConfigureAwait(false)
            : owner.Error;
    }
}

using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Directory;

/// <summary>
/// <c>PUT|PATCH /me/photo/$value</c> (and <c>/users/{caller}/photo/$value</c>): sets the signed-in user's photo.
/// </summary>
/// <remarks>
/// Graph takes any image. The photo store may keep less (Nextcloud: a square JPEG or PNG), so the writer crops and
/// converts (phase 27); an image it can't read is a 400 that says why. The image's own bytes decide its type, not the
/// request's <c>Content-Type</c> (Graph requires one, and clients send <c>image/jpeg</c> for PNGs too).
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

        if (image.IsEmpty)
        {
            return Error.Validation("The photo is empty.");
        }

        var owner = await PhotoOwner.SelfAsync(resolver, idOrUpn, cancellationToken).ConfigureAwait(false);
        return owner.IsSuccess
            ? await photos.SetAsync(owner.Value, image, cancellationToken).ConfigureAwait(false)
            : owner.Error;
    }
}

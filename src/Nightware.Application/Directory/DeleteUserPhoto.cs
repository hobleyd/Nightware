using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Directory;

/// <summary><c>DELETE /me/photo/$value</c> (and <c>/users/{caller}/photo/$value</c>): removes the signed-in user's photo.</summary>
public sealed class DeleteUserPhoto(IMailboxResolver resolver, IUserPhotoWriter photos)
{
    /// <param name="idOrUpn">Null for <c>/me</c>.</param>
    public async Task<Result> ExecuteAsync(string? idOrUpn, CancellationToken cancellationToken)
    {
        var owner = await PhotoOwner.SelfAsync(resolver, idOrUpn, cancellationToken).ConfigureAwait(false);
        return owner.IsSuccess
            ? await photos.DeleteAsync(owner.Value, cancellationToken).ConfigureAwait(false)
            : owner.Error;
    }
}

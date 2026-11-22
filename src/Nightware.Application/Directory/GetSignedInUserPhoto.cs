using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Directory;

/// <summary><c>GET /me/photo/$value</c>: the signed-in user's photo; <see cref="ErrorKind.NotFound"/> when they have none.</summary>
public sealed class GetSignedInUserPhoto(IMailboxResolver resolver, IUserPhotoReader photos)
{
    public async Task<Result<BlobContent>> ExecuteAsync(CancellationToken cancellationToken)
    {
        var me = await resolver.ResolveAsync(null, cancellationToken).ConfigureAwait(false);
        return me.IsSuccess
            ? await photos.GetAsync(me.Value.Address.ToLowerInvariant(), cancellationToken).ConfigureAwait(false)
            : me.Error;
    }
}

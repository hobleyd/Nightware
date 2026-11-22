using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Directory;

/// <summary>Whose photo a write targets: only the signed-in user's own (the photo store sets the caller's avatar).</summary>
internal static class PhotoOwner
{
    /// <summary>
    /// The caller's mailbox when <paramref name="idOrUpn"/> is null (<c>/me</c>) or names the caller; otherwise 403.
    /// Graph lets administrators set other users' photos; Nightware has no such right to hand on.
    /// </summary>
    public static async Task<Result<MailboxRef>> SelfAsync(IMailboxResolver resolver, string? idOrUpn, CancellationToken cancellationToken)
    {
        var me = await resolver.ResolveAsync(null, cancellationToken).ConfigureAwait(false);
        if (!me.IsSuccess || idOrUpn is null)
        {
            return me;
        }

        var target = await resolver.ResolveAsync(idOrUpn, cancellationToken).ConfigureAwait(false);
        if (!target.IsSuccess)
        {
            return target.Error.Kind == ErrorKind.Validation ? DirectoryUserLookup.Miss(idOrUpn) : target.Error;
        }

        return string.Equals(target.Value.Address, me.Value.Address, StringComparison.OrdinalIgnoreCase)
            ? me
            : Error.Forbidden("Only your own photo can be changed.");
    }
}

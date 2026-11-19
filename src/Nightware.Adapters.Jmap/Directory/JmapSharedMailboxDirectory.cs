using Nightware.Adapters.Jmap.Core;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Core.Requests;
using Nightware.Adapters.Jmap.Core.Session;
using Nightware.Adapters.Jmap.Mail.Write;
using Nightware.Application.Directory;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Directory;

/// <summary>
/// <see cref="ISharedMailboxDirectory"/> from the caller's JMAP session (phase 10): a shared mailbox the caller may
/// open is an account in their session named by its address, and its display name is the name of its identity. The
/// mail server decides who may open it; anyone else gets NotFound, so the profile tells nobody else it exists.
/// </summary>
internal sealed class JmapSharedMailboxDirectory(IJmapClient jmap) : ISharedMailboxDirectory
{
    public async Task<Result<DirectoryUser>> GetAsync(string address, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        string trimmed = address.Trim().ToLowerInvariant();
        Result<JmapAccount> account = await jmap.ResolveAccountAsync(new MailboxRef(trimmed), cancellationToken).ConfigureAwait(false);
        if (!account.IsSuccess)
        {
            return account.Error.Kind is ErrorKind.NotFound or ErrorKind.Forbidden ? Miss(trimmed) : account.Error;
        }

        // The caller's own mailbox is the directory's to describe; it did not know them.
        if (account.Value.IsPrimary)
        {
            return Miss(trimmed);
        }

        // The name is a nicety: a mailbox whose identities cannot be read is still described by its address.
        var request = new JmapRequestBuilder();
        var get = request.Add(JmapMethods.IdentityGet, new GetRequest(account.Value.AccountId));
        Result<JmapResponse> response = await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string? name = response.Bind(r => r.Get(get)).Match(
            r => SenderIdentity.Find(r.List, trimmed)?.Name,
            static _ => null);
        return new DirectoryUser(trimmed, string.IsNullOrWhiteSpace(name) ? trimmed : name.Trim(), trimmed) { Mail = trimmed };
    }

    private static Error Miss(string address) => Error.NotFound($"No shared mailbox '{address}' is open to the signed-in user.");
}

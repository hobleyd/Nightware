using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Entities;

/// <summary>
/// An entity's DNS host credential, sealed at rest (<see cref="ISecretSealer"/>, purpose bound to the host and the
/// entity, so a sealed credential copied to another entity or host does not open) and kept by the
/// <see cref="IEntityRegistry"/>. An entity has at most one: storing one forgets any other host's.
/// </summary>
public sealed class DnsTokenVault(IEntityRegistry entities, ISecretSealer sealer)
{
    /// <summary><c>dns-token:cloudflare:&lt;id&gt;</c> for Cloudflare, as tokens stored before other hosts existed were sealed.</summary>
    internal static string Purpose(DnsProvider provider, string entityId) => $"dns-token:{provider.Key()}:{entityId}";

    public Task<Result> StoreAsync(string entityId, DnsConnection? connection, CancellationToken cancellationToken) =>
        entities.SetSealedDnsTokenAsync(
            entityId,
            connection is null ? null : new SealedDnsToken(connection.Provider, sealer.Seal(Purpose(connection.Provider, entityId), connection.Token.Value)),
            cancellationToken);

    /// <summary>The entity's host and credential; Validation (target <c>dnsToken</c>) when it has none or it can no longer be read.</summary>
    public async Task<Result<DnsConnection>> OpenAsync(string entityId, CancellationToken cancellationToken)
    {
        Result<SealedDnsToken?> stored = await entities.GetSealedDnsTokenAsync(entityId, cancellationToken).ConfigureAwait(false);
        if (!stored.IsSuccess)
        {
            return stored.Error;
        }

        if (stored.Value is not { } sealedToken)
        {
            return Error.Validation("The entity has no DNS host connected. A global administrator connects one on the entity's DNS hosting page.", "dnsToken");
        }

        DnsProvider provider = sealedToken.Provider;
        return sealer.TryUnseal(Purpose(provider, entityId), sealedToken.Sealed, out string token) && DnsProviderToken.Parse(provider, token) is { IsSuccess: true } parsed
            ? new DnsConnection(provider, parsed.Value)
            : Error.Validation($"The entity's {provider.Name()} credential can no longer be read (the console's encryption keys changed). Enter it again on the entity's DNS hosting page.", "dnsToken");
    }
}

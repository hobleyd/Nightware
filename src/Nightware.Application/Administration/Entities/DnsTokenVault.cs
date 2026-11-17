using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Entities;

/// <summary>
/// An entity's DNS host token, sealed at rest (<see cref="ISecretSealer"/>, purpose bound to the entity, so a
/// sealed token copied to another entity does not open) and kept by the <see cref="IEntityRegistry"/>.
/// </summary>
public sealed class DnsTokenVault(IEntityRegistry entities, ISecretSealer sealer)
{
    internal static string Purpose(string entityId) => "dns-token:cloudflare:" + entityId;

    public Task<Result> StoreAsync(string entityId, DnsProviderToken? token, CancellationToken cancellationToken) =>
        entities.SetSealedDnsTokenAsync(entityId, token is null ? null : sealer.Seal(Purpose(entityId), token.Value), cancellationToken);

    /// <summary>The entity's token; Validation (target <c>dnsToken</c>) when it has none or it can no longer be read.</summary>
    public async Task<Result<DnsProviderToken>> OpenAsync(string entityId, CancellationToken cancellationToken)
    {
        Result<string?> stored = await entities.GetSealedDnsTokenAsync(entityId, cancellationToken).ConfigureAwait(false);
        if (!stored.IsSuccess)
        {
            return stored.Error;
        }

        if (stored.Value is null)
        {
            return Error.Validation("The entity has no Cloudflare token. A global administrator adds one on the entity's page.", "dnsToken");
        }

        return sealer.TryUnseal(Purpose(entityId), stored.Value, out string token) && DnsProviderToken.Parse(token) is { IsSuccess: true } parsed
            ? parsed.Value
            : Error.Validation("The entity's Cloudflare token can no longer be read (the console's encryption keys changed). Enter it again on the entity's page.", "dnsToken");
    }
}

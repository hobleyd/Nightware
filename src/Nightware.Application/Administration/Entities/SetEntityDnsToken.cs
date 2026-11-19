using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Entities;

/// <summary>Which of the entity's domains a DNS host token reaches, and in which zone.</summary>
public sealed record DnsTokenReach(IReadOnlyList<(MailDomain Domain, DnsZone Zone)> Reached, IReadOnlyList<MailDomain> Unreached);

/// <summary>
/// Connects an entity to a DNS host with the credential its domains' records are published with (replacing any
/// host it had), or (a blank token) disconnects it. The credential is tried first: one the host refuses, or one
/// that reaches none of the entity's domains, is not kept, and the entity stays as it was. Global administrators
/// only, like every domain change. The credential never reaches the audit trail.
/// </summary>
public sealed class SetEntityDnsToken(IAdminActor actor, IEntityRegistry entities, DnsHosts hosts, DnsTokenVault vault, IAuditLog audit)
{
    public async Task<Result<DnsTokenReach>> ExecuteAsync(string entityId, DnsProvider provider, string? token, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        Result allowed = AdminAuthorization.RequireGlobal(actor.Current);
        if (!allowed.IsSuccess)
        {
            return allowed.Error;
        }

        Result<Entity> entity = await entities.GetAsync(entityId, cancellationToken).ConfigureAwait(false);
        if (!entity.IsSuccess)
        {
            return entity.Error;
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            Result removed = await vault.StoreAsync(entityId, null, cancellationToken).ConfigureAwait(false);
            Record("entity.dns-token.removed", entityId, null, removed.IsSuccess, null);
            return removed.IsSuccess ? new DnsTokenReach([], []) : removed.Error;
        }

        Result<IDnsHosting> hosting = hosts.For(provider);
        if (!hosting.IsSuccess)
        {
            return hosting.Error;
        }

        Result<DnsProviderToken> parsed = DnsProviderToken.Parse(provider, token);
        if (!parsed.IsSuccess)
        {
            return parsed.Error;
        }

        // A host may refuse the credential for a domain it may not see (Gandi, Azure answer 403): that domain is
        // unreached when another is reached, and the refusal is the answer only when none is.
        var reached = new List<(MailDomain, DnsZone)>();
        var unreached = new List<MailDomain>();
        Error? refused = null;
        foreach (MailDomain domain in entity.Value.Domains)
        {
            Result<DnsZone> zone = await hosting.Value.FindZoneAsync(parsed.Value, domain, cancellationToken).ConfigureAwait(false);
            if (zone.IsSuccess)
            {
                reached.Add((domain, zone.Value));
            }
            else if (zone.Error.Kind == ErrorKind.NotFound || zone.Error is { Kind: ErrorKind.Validation, Target: "dnsToken" })
            {
                refused ??= zone.Error.Kind == ErrorKind.NotFound ? null : zone.Error;
                unreached.Add(domain);
            }
            else
            {
                Record("entity.dns-token.set", entityId, provider, false, null);
                return zone.Error;
            }
        }

        if (reached.Count == 0 && refused is not null)
        {
            Record("entity.dns-token.set", entityId, provider, false, null);
            return refused;
        }

        if (reached.Count == 0)
        {
            Record("entity.dns-token.set", entityId, provider, false, null);
            return Error.Validation(
                $"{provider.Name()} accepted the credential, but it reaches none of {string.Join(", ", unreached)}. Check that their zones are hosted there and that it may edit them.",
                "dnsToken");
        }

        Result stored = await vault.StoreAsync(entityId, new DnsConnection(provider, parsed.Value), cancellationToken).ConfigureAwait(false);
        Record("entity.dns-token.set", entityId, provider, stored.IsSuccess, string.Join(',', reached.Select(static r => r.Item2.Name).Distinct()));
        return stored.IsSuccess ? new DnsTokenReach(reached, unreached) : stored.Error;
    }

    private void Record(string action, string entityId, DnsProvider? provider, bool succeeded, string? zones) =>
        audit.Record(new AuditEvent(action, actor.Current.UserId, succeeded)
        {
            EntityId = entityId,
            Details = new Dictionary<string, string?> { ["provider"] = provider?.Key(), ["zones"] = zones },
        });
}

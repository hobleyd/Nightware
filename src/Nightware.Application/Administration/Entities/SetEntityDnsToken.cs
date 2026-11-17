using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Entities;

/// <summary>Which of the entity's domains a DNS host token reaches, and in which zone.</summary>
public sealed record DnsTokenReach(IReadOnlyList<(MailDomain Domain, DnsZone Zone)> Reached, IReadOnlyList<MailDomain> Unreached);

/// <summary>
/// Gives an entity the Cloudflare API token its domains' records are published with, or (a blank token) removes
/// it. The token is tried first: one Cloudflare refuses, or one that reaches none of the entity's domains, is not
/// kept. Global administrators only, like every domain change. The token never reaches the audit trail.
/// </summary>
public sealed class SetEntityDnsToken(IAdminActor actor, IEntityRegistry entities, IDnsHosting hosting, DnsTokenVault vault, IAuditLog audit)
{
    public async Task<Result<DnsTokenReach>> ExecuteAsync(string entityId, string? token, CancellationToken cancellationToken)
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
            Record("entity.dns-token.removed", entityId, removed.IsSuccess, null);
            return removed.IsSuccess ? new DnsTokenReach([], []) : removed.Error;
        }

        Result<DnsProviderToken> parsed = DnsProviderToken.Parse(token);
        if (!parsed.IsSuccess)
        {
            return parsed.Error;
        }

        var reached = new List<(MailDomain, DnsZone)>();
        var unreached = new List<MailDomain>();
        foreach (MailDomain domain in entity.Value.Domains)
        {
            Result<DnsZone> zone = await hosting.FindZoneAsync(parsed.Value, domain, cancellationToken).ConfigureAwait(false);
            if (zone.IsSuccess)
            {
                reached.Add((domain, zone.Value));
            }
            else if (zone.Error.Kind == ErrorKind.NotFound)
            {
                unreached.Add(domain);
            }
            else
            {
                Record("entity.dns-token.set", entityId, false, null);
                return zone.Error;
            }
        }

        if (reached.Count == 0)
        {
            Record("entity.dns-token.set", entityId, false, null);
            return Error.Validation(
                $"Cloudflare accepted the token, but it reaches none of {string.Join(", ", unreached)}. Give it the Zone / DNS / Edit permission on their zones.",
                "dnsToken");
        }

        Result stored = await vault.StoreAsync(entityId, parsed.Value, cancellationToken).ConfigureAwait(false);
        Record("entity.dns-token.set", entityId, stored.IsSuccess, string.Join(',', reached.Select(static r => r.Item2.Name).Distinct()));
        return stored.IsSuccess ? new DnsTokenReach(reached, unreached) : stored.Error;
    }

    private void Record(string action, string entityId, bool succeeded, string? zones) =>
        audit.Record(new AuditEvent(action, actor.Current.UserId, succeeded)
        {
            EntityId = entityId,
            Details = new Dictionary<string, string?> { ["provider"] = "cloudflare", ["zones"] = zones },
        });
}

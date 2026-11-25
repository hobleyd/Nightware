using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Entities;

/// <summary>The mail server's single MTA-STS policy (Stalwart <c>x:MtaSts</c>).</summary>
public interface IMtaStsPolicies
{
    /// <summary>The policy's mode, its own MX hosts and the mail server's default ones.</summary>
    Task<Result<MtaStsPolicy>> GetAsync(CancellationToken cancellationToken);

    /// <summary>Replaces the policy's own MX hosts with <paramref name="hosts"/>, then reloads the mail server's settings.</summary>
    Task<Result> SetMxHostsAsync(IReadOnlyList<string> hosts, CancellationToken cancellationToken);
}

/// <summary>Whether the MTA-STS policy lets senders deliver to a domain's MX hosts.</summary>
/// <param name="DomainMxHosts">The MX hosts the domain publishes (its records as the DNS page shows them).</param>
/// <param name="Missing">The ones the policy does not list; empty when it covers them all.</param>
public sealed record MtaStsCoverage(MailDomain Domain, MtaStsPolicy Policy, IReadOnlyList<string> DomainMxHosts, IReadOnlyList<string> Missing)
{
    /// <summary>A policy is served and leaves out one of the domain's MX hosts, so senders that apply it refuse or report delivery.</summary>
    public bool NeedsHosts => !Policy.IsDisabled && Missing.Count > 0;
}

/// <summary>
/// Compares the mail server's MTA-STS policy with one of an entity's domains: the policy serves every domain, so it
/// must list each domain's MX host. Any admin of the entity may read it.
/// </summary>
public sealed class GetMtaStsCoverage(GetEntityDnsRecords dns, IMtaStsPolicies policies)
{
    public async Task<Result<MtaStsCoverage>> ExecuteAsync(string entityId, string domain, CancellationToken cancellationToken)
    {
        Result<DomainDns> records = await dns.ExecuteAsync(entityId, domain, cancellationToken).ConfigureAwait(false);
        if (!records.IsSuccess)
        {
            return records.Error;
        }

        Result<MtaStsPolicy> policy = await policies.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!policy.IsSuccess)
        {
            return policy.Error;
        }

        IReadOnlyList<string> mx = MtaStsPolicy.MxHostsIn(records.Value.Records);
        return new MtaStsCoverage(records.Value.Domain, policy.Value, mx, policy.Value.Missing(mx));
    }
}

/// <summary>
/// Adds a domain's missing MX hosts to the mail server's MTA-STS policy. A policy without hosts of its own starts
/// from the mail server's defaults, so the domains they already cover stay covered. Global administrators only:
/// the policy is the whole server's. The policy's id changes with it, so the domain's <c>_mta-sts</c> record must
/// be published again afterwards (the DNS page then shows it as a change).
/// </summary>
public sealed class AddDomainToMtaStsPolicy(IAdminActor actor, GetMtaStsCoverage coverage, IMtaStsPolicies policies, IAuditLog audit)
{
    public async Task<Result<MtaStsCoverage>> ExecuteAsync(string entityId, string domain, CancellationToken cancellationToken)
    {
        Result allowed = AdminAuthorization.RequireGlobal(actor.Current);
        if (!allowed.IsSuccess)
        {
            return allowed.Error;
        }

        Result<MtaStsCoverage> before = await coverage.ExecuteAsync(entityId, domain, cancellationToken).ConfigureAwait(false);
        if (!before.IsSuccess)
        {
            return before.Error;
        }

        MtaStsCoverage now = before.Value;
        if (now.Policy.IsDisabled)
        {
            return Error.Validation("The mail server's MTA-STS policy is disabled, so there is no policy to add the domain to.", "domain");
        }

        if (now.Missing.Count == 0)
        {
            return now;
        }

        IReadOnlyList<string> hosts = now.Policy.Adding(now.Missing);
        Result set = await policies.SetMxHostsAsync(hosts, cancellationToken).ConfigureAwait(false);
        audit.Record(new AuditEvent("mail.mta-sts.mx-hosts", actor.Current.UserId, set.IsSuccess)
        {
            EntityId = entityId,
            Details = new Dictionary<string, string?> { ["domain"] = now.Domain.Name, ["added"] = string.Join(' ', now.Missing), ["hosts"] = string.Join(' ', hosts) },
        });
        if (!set.IsSuccess)
        {
            return set.Error;
        }

        MtaStsPolicy updated = now.Policy with { MxHosts = hosts };
        return now with { Policy = updated, Missing = updated.Missing(now.DomainMxHosts) };
    }
}

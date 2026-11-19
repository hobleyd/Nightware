using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Entities;

/// <summary>What cleaning a domain's mail records out of its zone at the entity's DNS host did.</summary>
/// <param name="Removed">Records deleted: those that matched what the console publishes for the domain.</param>
/// <param name="Left">Records at the domain's mail names that differ from what the console publishes, left in place.</param>
public sealed record DnsCleanUp(int Removed, int Left, IReadOnlyList<DnsChangeOutcome> Failed);

/// <summary>
/// Removes a domain's mail records from its zone at the entity's DNS host before the domain leaves its entity: exactly the
/// records the console publishes for it (MX, SPF, DKIM, DMARC and the rest of the mail server's set, with the
/// entity's own SPF and DMARC), matched by name, type and value. Anything else in the zone, including a mail record
/// someone changed by hand, is left. Global administrators only, with the entity's own token. Audited with counts.
/// </summary>
public sealed class RemoveDomainDnsRecords(IAdminActor actor, PlanDnsPublication planner, IAuditLog audit)
{
    public async Task<Result<DnsCleanUp>> ExecuteAsync(string entityId, string domain, CancellationToken cancellationToken)
    {
        Result<(DnsPublicationPreview Preview, DnsHostSession Host)> prepared = await planner.PrepareAsync(entityId, domain, cancellationToken).ConfigureAwait(false);
        if (!prepared.IsSuccess)
        {
            return prepared.Error;
        }

        (DnsPublicationPreview preview, DnsHostSession host) = prepared.Value;
        DnsPublicationPlan plan = preview.Plan;

        // Unchanged means the zone already holds exactly the record the console publishes: that is the console's.
        var failed = new List<DnsChangeOutcome>();
        int removed = 0;
        foreach (DnsChange published in plan.Changes.Where(static c => c.Kind == DnsChangeKind.Unchanged && c.Current is not null))
        {
            var removal = new DnsChange(DnsChangeKind.Remove, null, published.Current);
            Result applied = await host.ApplyAsync(plan.Zone, removal, cancellationToken).ConfigureAwait(false);
            if (applied.IsSuccess)
            {
                removed++;
            }
            else
            {
                failed.Add(new DnsChangeOutcome(removal, applied.Error));
            }
        }

        int left = plan.Changes.Count(static c => c.Kind == DnsChangeKind.Update);
        audit.Record(new AuditEvent("entity.domain.dns-removed", actor.Current.UserId, failed.Count == 0)
        {
            EntityId = entityId,
            Details = new Dictionary<string, string?>
            {
                ["domain"] = preview.Domain.Name,
                ["provider"] = host.Provider.Key(),
                ["zone"] = plan.Zone.Name,
                ["removed"] = removed.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["left"] = left.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["failed"] = failed.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
        });
        return new DnsCleanUp(removed, left, failed);
    }
}

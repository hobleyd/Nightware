using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Entities;

/// <param name="Error">Why the DNS host refused this change; null when it was made.</param>
public sealed record DnsChangeOutcome(DnsChange Change, Error? Error);

/// <summary>
/// What publishing did. When the zone or the records changed since the administrator looked
/// (<see cref="Stale"/>), nothing was written and <see cref="Preview"/> is the plan as it is now.
/// </summary>
public sealed record DnsPublication(DnsPublicationPreview Preview, bool Stale, IReadOnlyList<DnsChangeOutcome> Outcomes)
{
    public int Written => Outcomes.Count(static o => o.Error is null);

    public IReadOnlyList<DnsChangeOutcome> Failed => [.. Outcomes.Where(static o => o.Error is not null)];
}

/// <summary>
/// Publishes one of an entity's domains' records to its zone at the entity's DNS host: re-plans (never trusting the page), and
/// applies the plan only if it is the one the administrator saw (<paramref name="expectedFingerprint"/>). Each
/// change is made on its own; one the host refuses is reported and the rest still go ahead. Audited with counts.
/// </summary>
public sealed class PublishDnsRecords(IAdminActor actor, PlanDnsPublication planner, IAuditLog audit)
{
    public async Task<Result<DnsPublication>> ExecuteAsync(string entityId, string domain, string? expectedFingerprint, CancellationToken cancellationToken)
    {
        Result<(DnsPublicationPreview Preview, DnsHostSession Host)> prepared = await planner.PrepareAsync(entityId, domain, cancellationToken).ConfigureAwait(false);
        if (!prepared.IsSuccess)
        {
            return prepared.Error;
        }

        (DnsPublicationPreview preview, DnsHostSession host) = prepared.Value;
        DnsPublicationPlan plan = preview.Plan;
        if (!string.Equals(plan.Fingerprint, expectedFingerprint, StringComparison.Ordinal))
        {
            return new DnsPublication(preview, Stale: true, []);
        }

        var outcomes = new List<DnsChangeOutcome>();
        foreach (DnsChange change in plan.Changes.Where(static c => c.IsWrite))
        {
            Result applied = await host.ApplyAsync(plan.Zone, change, cancellationToken).ConfigureAwait(false);
            outcomes.Add(new DnsChangeOutcome(change, applied.IsSuccess ? null : applied.Error));
        }

        var publication = new DnsPublication(preview, Stale: false, outcomes);
        audit.Record(new AuditEvent("entity.domain.dns-published", actor.Current.UserId, publication.Failed.Count == 0)
        {
            EntityId = entityId,
            Details = new Dictionary<string, string?>
            {
                ["domain"] = preview.Domain.Name,
                ["provider"] = host.Provider.Key(),
                ["zone"] = plan.Zone.Name,
                ["created"] = Count(outcomes, DnsChangeKind.Create),
                ["updated"] = Count(outcomes, DnsChangeKind.Update),
                ["removed"] = Count(outcomes, DnsChangeKind.Remove),
                ["failed"] = publication.Failed.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            },
        });
        return publication;
    }

    private static string Count(List<DnsChangeOutcome> outcomes, DnsChangeKind kind) =>
        outcomes.Count(o => o.Error is null && o.Change.Kind == kind).ToString(System.Globalization.CultureInfo.InvariantCulture);
}

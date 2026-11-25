using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Reports;

/// <summary>
/// The DMARC aggregate and SMTP TLS reports the mail server received and analysed (reports sent to the address in a
/// domain's <c>rua=</c>), read with the console's mail-server credential. The mail server keeps them for a limited
/// time and the console stores none of them.
/// </summary>
public interface IMailReports
{
    /// <summary>Every report held, newest first as far as the mail server orders them.</summary>
    Task<Result<MailReports>> ListAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The reports for the domains the actor manages, and a summary per domain. <see cref="Domain"/> is the one domain
/// asked for, or null for all of them.
/// </summary>
public sealed record MailReportOverview(
    IReadOnlyList<string> Domains,
    string? Domain,
    IReadOnlyList<DomainReportSummary> Summaries,
    IReadOnlyList<DmarcAggregateReport> Dmarc,
    IReadOnlyList<TlsReport> Tls);

/// <summary>
/// Lists DMARC and TLS reports for the actor's domains: a global admin's are every entity's, an entity admin's
/// their own entities'. A report about another domain is never shown, and a TLS report covering several domains is
/// cut down to the actor's.
/// </summary>
public sealed class ListMailReports(IAdminActor actor, IEntityRegistry entities, IMailReports reports)
{
    public async Task<Result<MailReportOverview>> ExecuteAsync(string? domain, CancellationToken cancellationToken)
    {
        Result admin = AdminAuthorization.RequireAdmin(actor.Current);
        if (!admin.IsSuccess)
        {
            return admin.Error;
        }

        Result<IReadOnlyList<Entity>> all = await entities.ListAsync(cancellationToken).ConfigureAwait(false);
        if (!all.IsSuccess)
        {
            return all.Error;
        }

        List<string> domains = [.. all.Value.Where(e => actor.Current.MayManage(e.Id))
            .SelectMany(static e => e.Domains).Select(static d => d.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];

        string? selected = null;
        if (!string.IsNullOrWhiteSpace(domain))
        {
            selected = domains.FirstOrDefault(d => string.Equals(d, domain.Trim(), StringComparison.OrdinalIgnoreCase));
            if (selected is null)
            {
                return Error.Forbidden("You do not manage that domain.");
            }
        }

        if (domains.Count == 0)
        {
            return new MailReportOverview([], null, [], [], []);
        }

        Result<MailReports> held = await reports.ListAsync(cancellationToken).ConfigureAwait(false);
        if (!held.IsSuccess)
        {
            return held.Error;
        }

        IReadOnlyList<string> shown = selected is null ? domains : [selected];
        bool InScope(string reported) => shown.Any(d => ReportDomains.Covers(d, reported));

        var scoped = new MailReports(
            [.. held.Value.Dmarc.Where(r => InScope(r.Domain))
                .OrderByDescending(static r => r.PeriodStart).ThenByDescending(static r => r.ReceivedAt).ThenBy(static r => r.Organization, StringComparer.OrdinalIgnoreCase)],
            [.. held.Value.Tls.Select(r => r.Only(InScope)).OfType<TlsReport>()
                .OrderByDescending(static r => r.PeriodStart).ThenByDescending(static r => r.ReceivedAt).ThenBy(static r => r.Organization, StringComparer.OrdinalIgnoreCase)]);

        return new MailReportOverview(
            domains,
            selected,
            [.. shown.Select(d => DomainReportSummary.For(d, scoped))],
            scoped.Dmarc,
            scoped.Tls);
    }
}

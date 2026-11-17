using Microsoft.AspNetCore.Mvc;

using Nightware.Application.Administration;
using Nightware.Application.Administration.Entities;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Admin.Pages.Entities;

/// <summary>
/// The DNS records a domain needs, as the mail server generated them, with the domain's own state: a domain the
/// mail server does not serve, or serves without its signing keys, has no or incomplete records, and Repair is
/// offered here because it fixes exactly those two cases. The domain's SPF and DMARC records are the mail
/// server's unless the entity chose its own, which a global administrator edits here.
/// </summary>
public sealed class DnsModel(
    IAdminActor actor,
    GetEntity getEntity,
    GetEntityDnsRecords dns,
    AddEntityDomain repairDomain,
    SetDomainMailPolicy setMailPolicy) : ConsolePageModel(actor)
{
    [BindProperty(SupportsGet = true)]
    public string Id { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string Domain { get; set; } = string.Empty;

    public IReadOnlyList<DnsRecord> Records { get; private set; } = [];

    /// <summary>The domain's SPF and DMARC choice; null records are the mail server's default.</summary>
    public DomainMailPolicy Policy { get; private set; } = DomainMailPolicy.MailServerDefault;

    /// <summary>The SPF record to publish (the chosen one or the default), or null before the mail server serves the domain.</summary>
    public string? SpfInUse => MailDomain.TryParse(Domain, out MailDomain? d) ? DomainMailPolicy.SpfIn(d, Records) : null;

    /// <summary>The DMARC record to publish (the chosen one or the default), or null before the mail server serves the domain.</summary>
    public string? DmarcInUse => MailDomain.TryParse(Domain, out MailDomain? d) ? DomainMailPolicy.DmarcIn(d, Records) : null;

    /// <summary>The record a blank SPF field stands for, while the domain has no SPF choice.</summary>
    public string SpfPlaceholder => Policy.Spf is null ? SpfInUse ?? SpfRecord.Default.Value : string.Empty;

    /// <summary>The record a blank DMARC field stands for, while the domain has no DMARC choice.</summary>
    public string DmarcPlaceholder => Policy.Dmarc is null && MailDomain.TryParse(Domain, out MailDomain? d) ? DmarcInUse ?? DmarcRecord.Default(d).Value : string.Empty;

    /// <summary>The form's SPF field: blank means the mail server's default.</summary>
    [BindProperty]
    public string? Spf { get; set; }

    /// <summary>The form's DMARC field: blank means the mail server's default.</summary>
    [BindProperty]
    public string? Dmarc { get; set; }

    public EntityDetail Entity { get; private set; } = null!;

    /// <summary>The domain as the mail server sees it.</summary>
    public DomainStatus Status { get; private set; } = null!;

    /// <summary>Repair would change something: the domain is not served, or is served without its keys.</summary>
    public bool Repairable => Status.Mail.State != LinkState.Linked || !Status.DkimPresent;

    public Task<IActionResult> OnGetAsync(CancellationToken cancellationToken) => RenderAsync(cancellationToken);

    public async Task<IActionResult> OnPostRepairAsync(CancellationToken cancellationToken)
    {
        Result<DomainStatus> repaired = await repairDomain.ExecuteAsync(Id, Domain, cancellationToken);
        if (repaired.IsSuccess)
        {
            Notice = Systems.DomainRepairNotice(repaired.Value);
            return RedirectToPage(new { id = Id, domain = Domain });
        }

        return Fail(repaired.Error) ?? await RenderAsync(cancellationToken);
    }

    public Task<IActionResult> OnPostMailPolicyAsync(CancellationToken cancellationToken) =>
        SetMailPolicyAsync(Spf, Dmarc, cancellationToken);

    public Task<IActionResult> OnPostDefaultMailPolicyAsync(CancellationToken cancellationToken) =>
        SetMailPolicyAsync(null, null, cancellationToken);

    private async Task<IActionResult> SetMailPolicyAsync(string? spf, string? dmarc, CancellationToken cancellationToken)
    {
        Result<DomainMailPolicy> set = await setMailPolicy.ExecuteAsync(Id, Domain, spf, dmarc, cancellationToken);
        if (set.IsSuccess)
        {
            Notice = set.Value.IsDefault
                ? $"{Domain} uses the mail server's SPF and DMARC records. Update them at your DNS provider."
                : $"SPF and DMARC records for {Domain} saved. Update them at your DNS provider.";
            return RedirectToPage(new { id = Id, domain = Domain });
        }

        // Re-rendered with what was typed, so the mistake can be fixed in place.
        return Fail(set.Error) ?? await RenderAsync(cancellationToken, keepForm: true);
    }

    private async Task<IActionResult> RenderAsync(CancellationToken cancellationToken, bool keepForm = false)
    {
        if (!TryGet(await getEntity.ExecuteAsync(Id, cancellationToken), out EntityDetail entity, out IActionResult? failure))
        {
            return failure!;
        }

        Entity = entity;
        if (entity.Domains.FirstOrDefault(d => string.Equals(d.Domain.Name, Domain, StringComparison.OrdinalIgnoreCase)) is not { } status)
        {
            return NotFound();
        }

        Status = status;
        Policy = entity.Entity.MailPolicyFor(status.Domain);
        if (!keepForm)
        {
            Spf = Policy.Spf?.Value;
            Dmarc = Policy.Dmarc?.Value;
        }

        // A domain the mail server does not serve has no records to read; asking would only fail.
        if (status.Mail.State == LinkState.Missing)
        {
            return Page();
        }

        if (!TryGet(await dns.ExecuteAsync(Id, Domain, cancellationToken), out DomainDns domainDns, out failure))
        {
            return failure!;
        }

        Records = domainDns.Records;
        return Page();
    }
}

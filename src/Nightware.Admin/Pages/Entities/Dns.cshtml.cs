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
/// server's unless the entity chose its own, which a global administrator edits here. When the entity is connected
/// to Cloudflare, a global administrator sees what publishing would change in the zone and publishes it here.
/// </summary>
public sealed class DnsModel(
    IAdminActor actor,
    GetEntity getEntity,
    GetEntityDnsRecords dns,
    AddEntityDomain repairDomain,
    SetDomainMailPolicy setMailPolicy,
    PlanDnsPublication planPublication,
    SuggestMailAuthentication suggest,
    PublishDnsRecords publish) : ConsolePageModel(actor)
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

    /// <summary>The mail server's SPF record the domain uses while it has no SPF choice of its own; null once it has one.</summary>
    public string? SpfDefaultInUse => Policy.Spf is null ? SpfInUse ?? SpfRecord.Default.Value : null;

    /// <summary>The mail server's DMARC record the domain uses while it has no DMARC choice of its own; null once it has one.</summary>
    public string? DmarcDefaultInUse => Policy.Dmarc is null && MailDomain.TryParse(Domain, out MailDomain? d) ? DmarcInUse ?? DmarcRecord.Default(d).Value : null;

    /// <summary>The <c>p=</c> of the DMARC record in use (none, quarantine or reject), or null when it cannot be read.</summary>
    public string? DmarcPolicyInUse => (Policy.Dmarc?.Value ?? DmarcDefaultInUse) is { } record && DmarcRecord.Parse(record) is { IsSuccess: true } parsed ? parsed.Value.Policy : null;

    /// <summary>The form's SPF field: blank means the mail server's default.</summary>
    [BindProperty]
    public string? Spf { get; set; }

    /// <summary>The administrator asked to change SPF and DMARC (<c>?edit=true</c>) although the domain has its own.</summary>
    [BindProperty(SupportsGet = true)]
    public bool Edit { get; set; }

    /// <summary>A save of SPF and DMARC is being re-shown, to be corrected or confirmed.</summary>
    private bool _policyPosted;

    /// <summary>
    /// The SPF and DMARC form is a step only while the domain has not chosen both records of its own, on Edit, or
    /// while a save is corrected; otherwise the records stand read-only with an Edit link.
    /// </summary>
    public bool ShowPolicyForm => Edit || _policyPosted || Policy.Spf is null || Policy.Dmarc is null;

    /// <summary>The box confirming a change to the SPF record, which decides who may send the domain's mail.</summary>
    [BindProperty]
    public bool ConfirmSpf { get; set; }

    /// <summary>
    /// The SPF field holds a record other than the domain's own (a suggestion, or an edit): saving it changes who
    /// may send the domain's mail, so it needs <see cref="ConfirmSpf"/>.
    /// </summary>
    public bool SpfChanges => !string.IsNullOrWhiteSpace(Spf) && Spf.Trim() != Policy.Spf?.Value;

    /// <summary>What saving the SPF field means, in words, for the confirming box.</summary>
    public string SpfConsequence => SpfSuggested && Suggestion is { Addresses.Count: > 0 } s
        ? $"Only {string.Join(", ", s.Addresses)} may send mail for {Domain}; other senders are refused"
        : $"Use this SPF record for {Domain}: senders it does not list fail SPF";

    /// <summary>The form's DMARC field: blank means the mail server's default.</summary>
    [BindProperty]
    public string? Dmarc { get; set; }

    public EntityDetail Entity { get; private set; } = null!;

    /// <summary>
    /// The hardened SPF and DMARC offered (prefilled, not yet in use) where the domain has no record of its own;
    /// global administrators only, null otherwise.
    /// </summary>
    public MailAuthenticationSuggestion? Suggestion { get; private set; }

    /// <summary>The SPF field holds the suggestion rather than a record of the domain's own.</summary>
    public bool SpfSuggested => Suggestion is not null && Policy.Spf is null && Spf == Suggestion.Spf.Value;

    /// <summary>The DMARC field holds the suggestion rather than a record of the domain's own.</summary>
    public bool DmarcSuggested => Suggestion is not null && Policy.Dmarc is null && Dmarc == Suggestion.Dmarc.Value;

    /// <summary>What publishing to Cloudflare would change; null when not offered or not readable (<see cref="PublicationUnread"/>).</summary>
    public DnsPublicationPreview? Publication { get; private set; }

    /// <summary>Why the Cloudflare zone could not be compared, shown in place of the preview.</summary>
    public Error? PublicationUnread { get; private set; }

    /// <summary>The box confirming a plan that removes another server's MX, moving all of the domain's mail here.</summary>
    [BindProperty]
    public bool ConfirmMailMove { get; set; }

    /// <summary>The plan the administrator saw; publishing applies only that plan.</summary>
    [BindProperty]
    public string? PlanFingerprint { get; set; }

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

    public async Task<IActionResult> OnPostPublishAsync(CancellationToken cancellationToken)
    {
        // Removing another server's MX sends all of the domain's mail here and none there: confirmed like any change
        // to where mail goes. The plan is read again, as the publish itself does.
        if (!ConfirmMailMove && await planPublication.ExecuteAsync(Id, Domain, cancellationToken) is { IsSuccess: true } preview
            && preview.Value.Plan.MailServersRemoved.Count > 0)
        {
            ModelState.AddModelError(string.Empty, $"Tick the box to confirm that all mail for {Domain} should come here.");
            return await RenderAsync(cancellationToken);
        }

        Result<DnsPublication> published = await publish.ExecuteAsync(Id, Domain, PlanFingerprint, cancellationToken);
        if (!published.IsSuccess)
        {
            return Fail(published.Error) ?? await RenderAsync(cancellationToken);
        }

        DnsPublication outcome = published.Value;
        if (outcome.Stale)
        {
            ModelState.AddModelError(string.Empty, "The records or the Cloudflare zone changed since the page was loaded, so nothing was published. Check the changes below and publish again.");
            return await RenderAsync(cancellationToken);
        }

        string written = $"{outcome.Written} {(outcome.Written == 1 ? "change" : "changes")} published to Cloudflare for {Domain}.";
        if (outcome.Failed.Count == 0)
        {
            Notice = written;
            return RedirectToPage(new { id = Id, domain = Domain });
        }

        ModelState.AddModelError(string.Empty, $"{written} Cloudflare refused {outcome.Failed.Count}:");
        foreach (DnsChangeOutcome failed in outcome.Failed)
        {
            DnsRecord? record = failed.Change.Desired ?? failed.Change.Current?.Record;
            ModelState.AddModelError(string.Empty, $"{record?.Type} {record?.Name}: {failed.Error!.Message}");
        }

        return await RenderAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostMailPolicyAsync(CancellationToken cancellationToken)
    {
        _policyPosted = true;
        // A wrong SPF with -all stops the domain's outgoing mail, so changing it is confirmed like any change to who
        // can get in. Saving only DMARC, with SPF as it was, needs no box.
        // Only global administrators may set records at all; anyone else is refused by the use case, not asked to confirm.
        if (Actor.IsGlobal && !ConfirmSpf && await getEntity.ExecuteAsync(Id, cancellationToken) is { IsSuccess: true } entity
            && entity.Value.Domains.FirstOrDefault(d => string.Equals(d.Domain.Name, Domain, StringComparison.OrdinalIgnoreCase)) is { } status)
        {
            Policy = entity.Value.Entity.MailPolicyFor(status.Domain);
            if (SpfChanges)
            {
                ModelState.AddModelError(string.Empty, "Tick the box to confirm the SPF record: it decides who may send mail for " + Domain + ".");
                return await RenderAsync(cancellationToken, keepForm: true);
            }
        }

        return await SetMailPolicyAsync(Spf, Dmarc, cancellationToken);
    }

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
        if (Actor.IsGlobal && (Policy.Spf is null || Policy.Dmarc is null))
        {
            // Never fails the page: without a suggestion the fields are simply left empty.
            Result<MailAuthenticationSuggestion> suggested = await suggest.ExecuteAsync(Id, Domain, cancellationToken);
            Suggestion = suggested.IsSuccess ? suggested.Value : null;
        }

        if (!keepForm)
        {
            Spf = Policy.Spf?.Value ?? Suggestion?.Spf.Value;
            Dmarc = Policy.Dmarc?.Value ?? Suggestion?.Dmarc.Value;
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
        if (Actor.IsGlobal && entity.Entity.HasDnsToken && Records.Count > 0)
        {
            // Never fails the page: the records are still worth showing when Cloudflare cannot be read.
            Result<DnsPublicationPreview> preview = await planPublication.ExecuteAsync(Id, Domain, cancellationToken);
            Publication = preview.IsSuccess ? preview.Value : null;
            PublicationUnread = preview.IsSuccess ? null : preview.Error;
            // The plan just read, not the one posted: the hidden field would otherwise echo the posted value.
            ModelState.Remove(nameof(PlanFingerprint));
            PlanFingerprint = Publication?.Plan.Fingerprint;
        }

        return Page();
    }
}

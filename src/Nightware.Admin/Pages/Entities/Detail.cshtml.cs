using Microsoft.AspNetCore.Mvc;

using Nightware.Application.Administration;
using Nightware.Application.Administration.Entities;
using Nightware.Application.Administration.Users;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Admin.Pages.Entities;

/// <summary>One entity: its domains (with repair and removal) and a way to its users. Its DNS hosting and administrators are pages of their own.</summary>
public sealed class DetailModel(
    IAdminActor actor,
    GetEntity getEntity,
    ListUsers listUsers,
    AddEntityDomain addDomain,
    RemoveEntityDomain removeDomain,
    RemoveDomainDnsRecords removeDnsRecords) : ConsolePageModel(actor)
{
    [BindProperty(SupportsGet = true)]
    public string Id { get; set; } = string.Empty;

    public EntityDetail Entity { get; private set; } = null!;

    public IReadOnlyList<UserDetail> Members { get; private set; } = [];

    /// <summary>How many of the entity's addresses (primary and aliases) are on the domain; such a domain cannot be removed.</summary>
    public int AddressesOn(MailDomain domain) =>
        Members.Sum(m => m.User.Identity.Aliases.Prepend(m.User.Identity.Email).Count(a => a.EndsWith("@" + domain.Name, StringComparison.OrdinalIgnoreCase)));

    public Task<IActionResult> OnGetAsync(CancellationToken cancellationToken) => RenderAsync(cancellationToken);

    public async Task<IActionResult> OnPostAddDomainAsync(string domain, CancellationToken cancellationToken) =>
        await AfterAsync(await addDomain.ExecuteAsync(Id, domain, cancellationToken), s => s.Mail.State == LinkState.Linked
            ? $"{s.Domain} added. Publish its DNS records."
            : $"{s.Domain} added in Keycloak; the mail server does not serve it yet ({s.Mail.Detail}). Try Repair.", cancellationToken);

    public async Task<IActionResult> OnPostRepairDomainAsync(string domain, CancellationToken cancellationToken) =>
        await AfterAsync(await addDomain.ExecuteAsync(Id, domain, cancellationToken), Systems.DomainRepairNotice, cancellationToken);

    /// <summary>
    /// Removes a domain, confirmed in the row's pop-up. With <paramref name="cleanUpDns"/>, the domain's mail records
    /// come out of its zone at the entity's DNS host first (only those matching what the console publishes); if that fails, the
    /// domain stays, so its records can still be found and cleaned up.
    /// </summary>
    public async Task<IActionResult> OnPostRemoveDomainAsync(string domain, bool confirm, bool cleanUpDns, CancellationToken cancellationToken)
    {
        if (!confirm)
        {
            return await UnconfirmedAsync("Confirm removing the domain in its row.", cancellationToken);
        }

        string cleaned = string.Empty;
        if (cleanUpDns)
        {
            Result<DnsCleanUp> cleanUp = await removeDnsRecords.ExecuteAsync(Id, domain, cancellationToken);
            if (!cleanUp.IsSuccess)
            {
                return Fail(cleanUp.Error) ?? await UnconfirmedAsync($"Nothing was removed: the mail records for {domain} could not be cleaned up at the DNS host.", cancellationToken);
            }

            if (cleanUp.Value.Failed.Count > 0)
            {
                ModelState.AddModelError(string.Empty, $"{domain} was not removed: the DNS host refused to delete {cleanUp.Value.Failed.Count} of its mail records ({cleanUp.Value.Failed[0].Error!.Message}). {cleanUp.Value.Removed} were deleted; try again.");
                return await RenderAsync(cancellationToken);
            }

            cleaned = $" {cleanUp.Value.Removed} mail {(cleanUp.Value.Removed == 1 ? "record was" : "records were")} removed from the DNS host"
                + (cleanUp.Value.Left > 0 ? $"; {cleanUp.Value.Left} that differ from what the console publishes were left." : ".");
        }

        return await AfterAsync(await removeDomain.ExecuteAsync(Id, domain, cancellationToken), _ => $"{domain} removed from the entity and the mail server.{cleaned}", cancellationToken);
    }

    private async Task<IActionResult> UnconfirmedAsync(string message, CancellationToken cancellationToken)
    {
        ModelState.AddModelError(string.Empty, message);
        return await RenderAsync(cancellationToken);
    }

    private async Task<IActionResult> AfterAsync<T>(Result<T> result, Func<T, string> notice, CancellationToken cancellationToken)
    {
        if (result.IsSuccess)
        {
            Notice = notice(result.Value);
            return RedirectToPage(new { id = Id });
        }

        return Fail(result.Error) ?? await RenderAsync(cancellationToken);
    }

    private async Task<IActionResult> RenderAsync(CancellationToken cancellationToken)
    {
        if (!TryGet(await getEntity.ExecuteAsync(Id, cancellationToken), out EntityDetail entity, out IActionResult? failure)
            || !TryGet(await listUsers.ExecuteAsync(Id, cancellationToken), out IReadOnlyList<UserDetail> members, out failure))
        {
            return failure!;
        }

        Entity = entity;
        Members = members;
        return Page();
    }
}

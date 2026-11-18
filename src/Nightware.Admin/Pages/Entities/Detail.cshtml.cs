using Microsoft.AspNetCore.Mvc;

using Nightware.Application.Administration;
using Nightware.Application.Administration.Entities;
using Nightware.Application.Administration.Users;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Admin.Pages.Entities;

/// <summary>One entity: its domains (with repair), its Cloudflare token, its administrators, and a way to its users.</summary>
public sealed class DetailModel(
    IAdminActor actor,
    GetEntity getEntity,
    ListEntityAdmins listAdmins,
    ListUsers listUsers,
    AddEntityDomain addDomain,
    RemoveEntityDomain removeDomain,
    RemoveDomainDnsRecords removeDnsRecords,
    GrantEntityAdmin grantAdmin,
    RevokeEntityAdmin revokeAdmin,
    SetEntityDnsToken setDnsToken) : ConsolePageModel(actor)
{
    [BindProperty(SupportsGet = true)]
    public string Id { get; set; } = string.Empty;

    public EntityDetail Entity { get; private set; } = null!;

    public IReadOnlyList<IdentityAccount> Admins { get; private set; } = [];

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
    /// come out of its Cloudflare zone first (only those matching what the console publishes); if that fails, the
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
                return Fail(cleanUp.Error) ?? await UnconfirmedAsync($"Nothing was removed: the mail records for {domain} could not be cleaned up at Cloudflare.", cancellationToken);
            }

            if (cleanUp.Value.Failed.Count > 0)
            {
                ModelState.AddModelError(string.Empty, $"{domain} was not removed: Cloudflare refused to delete {cleanUp.Value.Failed.Count} of its mail records ({cleanUp.Value.Failed[0].Error!.Message}). {cleanUp.Value.Removed} were deleted; try again.");
                return await RenderAsync(cancellationToken);
            }

            cleaned = $" {cleanUp.Value.Removed} mail {(cleanUp.Value.Removed == 1 ? "record was" : "records were")} removed from Cloudflare"
                + (cleanUp.Value.Left > 0 ? $"; {cleanUp.Value.Left} that differ from what the console publishes were left." : ".");
        }

        return await AfterAsync(await removeDomain.ExecuteAsync(Id, domain, cancellationToken), _ => $"{domain} removed from the entity. The mail server keeps the domain.{cleaned}", cancellationToken);
    }

    public async Task<IActionResult> OnPostGrantAdminAsync(string userId, bool confirm, CancellationToken cancellationToken) =>
        !confirm
            ? await UnconfirmedAsync("Tick the box to confirm the new administrator.", cancellationToken)
            : await AfterAsync(await grantAdmin.ExecuteAsync(Id, userId, cancellationToken), "Administrator added. They must set up an authenticator app at their next sign-in.", cancellationToken);

    public async Task<IActionResult> OnPostRevokeAdminAsync(string userId, bool confirm, CancellationToken cancellationToken) =>
        !confirm
            ? await UnconfirmedAsync("Tick the box to confirm removing the administrator.", cancellationToken)
            : await AfterAsync(await revokeAdmin.ExecuteAsync(Id, userId, cancellationToken), "Administrator removed.", cancellationToken);

    /// <summary>The token is a handler argument, never a bound property, so a failed save cannot render it back.</summary>
    public async Task<IActionResult> OnPostDnsTokenAsync(string? dnsToken, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(dnsToken)
            ? await UnconfirmedAsync("Paste the Cloudflare API token.", cancellationToken)
            : await AfterAsync(await setDnsToken.ExecuteAsync(Id, dnsToken, cancellationToken), DnsTokenNotice, cancellationToken);

    public async Task<IActionResult> OnPostRemoveDnsTokenAsync(bool confirm, CancellationToken cancellationToken) =>
        !confirm
            ? await UnconfirmedAsync("Tick the box to confirm disconnecting Cloudflare.", cancellationToken)
            : await AfterAsync(await setDnsToken.ExecuteAsync(Id, null, cancellationToken), _ => "Cloudflare disconnected. The records already published stay in Cloudflare.", cancellationToken);

    internal static string DnsTokenNotice(DnsTokenReach reach)
    {
        string reached = string.Join(", ", reach.Reached.Select(static r => r.Domain.Name == r.Zone.Name ? r.Domain.Name : $"{r.Domain} (zone {r.Zone.Name})"));
        string notice = $"Cloudflare token saved. It reaches {reached}; publish each domain's records from its DNS page.";
        return reach.Unreached.Count == 0 ? notice : $"{notice} It does not reach {string.Join(", ", reach.Unreached)}: publish those by hand or widen the token.";
    }

    private async Task<IActionResult> UnconfirmedAsync(string message, CancellationToken cancellationToken)
    {
        ModelState.AddModelError(string.Empty, message);
        return await RenderAsync(cancellationToken);
    }

    private Task<IActionResult> AfterAsync(Result result, string notice, CancellationToken cancellationToken) =>
        AfterAsync(result.IsSuccess ? Result<bool>.Success(true) : result.Error, _ => notice, cancellationToken);

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
            || !TryGet(await listAdmins.ExecuteAsync(Id, cancellationToken), out IReadOnlyList<IdentityAccount> admins, out failure)
            || !TryGet(await listUsers.ExecuteAsync(Id, cancellationToken), out IReadOnlyList<UserDetail> members, out failure))
        {
            return failure!;
        }

        Entity = entity;
        Admins = admins;
        Members = members;
        return Page();
    }
}

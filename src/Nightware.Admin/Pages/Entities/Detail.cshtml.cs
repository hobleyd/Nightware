using Microsoft.AspNetCore.Mvc;

using Nightware.Application.Administration;
using Nightware.Application.Administration.Entities;
using Nightware.Application.Administration.Users;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Admin.Pages.Entities;

/// <summary>One entity: its domains (with repair), its administrators, and a way to its users.</summary>
public sealed class DetailModel(
    IAdminActor actor,
    GetEntity getEntity,
    ListEntityAdmins listAdmins,
    ListUsers listUsers,
    AddEntityDomain addDomain,
    RemoveEntityDomain removeDomain,
    GrantEntityAdmin grantAdmin,
    RevokeEntityAdmin revokeAdmin) : ConsolePageModel(actor)
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

    public async Task<IActionResult> OnPostRemoveDomainAsync(string domain, bool confirm, CancellationToken cancellationToken) =>
        !confirm
            ? await UnconfirmedAsync("Tick the box to confirm removing the domain.", cancellationToken)
            : await AfterAsync(await removeDomain.ExecuteAsync(Id, domain, cancellationToken), _ => $"{domain} removed from the entity. The mail server keeps the domain.", cancellationToken);

    public async Task<IActionResult> OnPostGrantAdminAsync(string userId, bool confirm, CancellationToken cancellationToken) =>
        !confirm
            ? await UnconfirmedAsync("Tick the box to confirm the new administrator.", cancellationToken)
            : await AfterAsync(await grantAdmin.ExecuteAsync(Id, userId, cancellationToken), "Administrator added. They must set up an authenticator app at their next sign-in.", cancellationToken);

    public async Task<IActionResult> OnPostRevokeAdminAsync(string userId, bool confirm, CancellationToken cancellationToken) =>
        !confirm
            ? await UnconfirmedAsync("Tick the box to confirm removing the administrator.", cancellationToken)
            : await AfterAsync(await revokeAdmin.ExecuteAsync(Id, userId, cancellationToken), "Administrator removed.", cancellationToken);

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

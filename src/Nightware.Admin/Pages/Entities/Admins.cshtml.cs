using Microsoft.AspNetCore.Mvc;

using Nightware.Application.Administration;
using Nightware.Application.Administration.Entities;
using Nightware.Application.Administration.Users;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Admin.Pages.Entities;

/// <summary>An entity's administrators: who they are, making one of its users an administrator, and removing one.</summary>
public sealed class AdminsModel(
    IAdminActor actor,
    GetEntity getEntity,
    ListEntityAdmins listAdmins,
    ListUsers listUsers,
    GrantEntityAdmin grantAdmin,
    RevokeEntityAdmin revokeAdmin) : ConsolePageModel(actor)
{
    [BindProperty(SupportsGet = true)]
    public string Id { get; set; } = string.Empty;

    public EntityDetail Entity { get; private set; } = null!;

    public IReadOnlyList<IdentityAccount> Admins { get; private set; } = [];

    public IReadOnlyList<UserDetail> Members { get; private set; } = [];

    public Task<IActionResult> OnGetAsync(CancellationToken cancellationToken) => RenderAsync(cancellationToken);

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

    private async Task<IActionResult> AfterAsync(Result result, string notice, CancellationToken cancellationToken)
    {
        if (result.IsSuccess)
        {
            Notice = notice;
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

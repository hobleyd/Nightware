using Microsoft.AspNetCore.Mvc;

using Nightware.Application.Administration;
using Nightware.Application.Administration.Admins;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Admin.Pages.Admins;

/// <summary>Global administrators (global admins only). Rights are granted from a user's own page.</summary>
public sealed class IndexModel(IAdminActor actor, ListGlobalAdmins list, SetGlobalAdmin set) : ConsolePageModel(actor)
{
    public IReadOnlyList<IdentityAccount> Admins { get; private set; } = [];

    public Task<IActionResult> OnGetAsync(CancellationToken cancellationToken) => RenderAsync(cancellationToken);

    public async Task<IActionResult> OnPostRevokeAsync(string userId, CancellationToken cancellationToken)
    {
        Result revoked = await set.ExecuteAsync(userId, false, cancellationToken);
        if (revoked.IsSuccess)
        {
            Notice = "Global administrator rights removed.";
            return RedirectToPage();
        }

        return Fail(revoked.Error) ?? await RenderAsync(cancellationToken);
    }

    private async Task<IActionResult> RenderAsync(CancellationToken cancellationToken)
    {
        if (!TryGet(await list.ExecuteAsync(cancellationToken), out IReadOnlyList<IdentityAccount> admins, out IActionResult? failure))
        {
            return failure!;
        }

        Admins = admins;
        return Page();
    }
}

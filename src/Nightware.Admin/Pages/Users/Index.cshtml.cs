using Microsoft.AspNetCore.Mvc;

using Nightware.Application.Administration;
using Nightware.Application.Administration.Entities;
using Nightware.Application.Administration.Users;

namespace Nightware.Admin.Pages.Users;

/// <summary>An entity's users with their state in each backend.</summary>
public sealed class IndexModel(IAdminActor actor, GetEntity getEntity, ListUsers listUsers) : ConsolePageModel(actor)
{
    [BindProperty(SupportsGet = true)]
    public string Entity { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    public EntityDetail Detail { get; private set; } = null!;

    public IReadOnlyList<UserDetail> Users { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!TryGet(await getEntity.ExecuteAsync(Entity, cancellationToken), out EntityDetail detail, out IActionResult? failure)
            || !TryGet(await listUsers.ExecuteAsync(Entity, cancellationToken), out IReadOnlyList<UserDetail> users, out failure))
        {
            return failure!;
        }

        Detail = detail;
        string? q = Q?.Trim();
        Users = string.IsNullOrEmpty(q)
            ? users
            : [.. users.Where(u => u.User.Identity.Email.Contains(q, StringComparison.OrdinalIgnoreCase)
                || u.User.Identity.Username.Contains(q, StringComparison.OrdinalIgnoreCase)
                || u.User.Identity.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase))];
        return Page();
    }
}

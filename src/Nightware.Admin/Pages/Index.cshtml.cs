using Microsoft.AspNetCore.Mvc;

using Nightware.Application.Administration;
using Nightware.Application.Administration.Entities;
using Nightware.Application.Administration.Status;
using Nightware.Domain.Administration;

namespace Nightware.Admin.Pages;

/// <summary>Dashboard: what needs the admin, backend health, and the actor's entities.</summary>
public sealed class IndexModel(IAdminActor actor, GetAttention attention, GetSystemStatus status, ListEntities entities) : ConsolePageModel(actor)
{
    public Attention Attention { get; private set; } = new([], [], []);

    public IReadOnlyList<BackendHealth> Health { get; private set; } = [];

    public IReadOnlyList<Entity> Entities { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!TryGet(await status.ExecuteAsync(cancellationToken), out IReadOnlyList<BackendHealth> health, out IActionResult? failure)
            || !TryGet(await entities.ExecuteAsync(cancellationToken), out IReadOnlyList<Entity> list, out failure)
            || !TryGet(await attention.ExecuteAsync(cancellationToken), out Attention work, out failure))
        {
            return failure!;
        }

        Attention = work;
        Health = health;
        Entities = list;
        return Page();
    }
}

using Microsoft.AspNetCore.Mvc;

using Nightware.Application.Administration;
using Nightware.Application.Administration.Groups;
using Nightware.Domain.Administration;

namespace Nightware.Admin.Pages.Entities.Groups;

/// <summary>An entity's groups (phase 9): distribution lists and Nextcloud groups, by name.</summary>
public sealed class IndexModel(IAdminActor actor, ListGroups listGroups) : ConsolePageModel(actor)
{
    [BindProperty(SupportsGet = true)]
    public string Id { get; set; } = string.Empty;

    public Entity Entity { get; private set; } = null!;

    public IReadOnlyList<Group> Groups { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!TryGet(await listGroups.ExecuteAsync(Id, cancellationToken), out (Entity Entity, IReadOnlyList<Group> Groups) listed, out IActionResult? failure))
        {
            return failure!;
        }

        (Entity, Groups) = listed;
        return Page();
    }
}

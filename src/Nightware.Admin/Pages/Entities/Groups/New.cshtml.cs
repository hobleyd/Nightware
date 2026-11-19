using Microsoft.AspNetCore.Mvc;

using Nightware.Application.Administration;
using Nightware.Application.Administration.Groups;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Admin.Pages.Entities.Groups;

/// <summary>Creates a group of an entity; its list and Nextcloud group are built straight away.</summary>
public sealed class NewModel(IAdminActor actor, ListGroups listGroups, CreateGroup createGroup) : ConsolePageModel(actor)
{
    [BindProperty(SupportsGet = true)]
    public string Id { get; set; } = string.Empty;

    [BindProperty]
    public GroupInput Input { get; set; } = new() { Mail = true, Files = true };

    public Entity Entity { get; private set; } = null!;

    public Task<IActionResult> OnGetAsync(CancellationToken cancellationToken) => RenderAsync(cancellationToken);

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        Result<GroupSyncOutcome> created = await createGroup.ExecuteAsync(Id, Input.ToForm(), cancellationToken);
        if (created.IsSuccess)
        {
            Notice = string.Join(" ", created.Value.Notes.Prepend("Group created."));
            return RedirectToPage("/Entities/Groups/Detail", new { id = Id, groupId = created.Value.Group.Id });
        }

        return Fail(created.Error) ?? await RenderAsync(cancellationToken);
    }

    private async Task<IActionResult> RenderAsync(CancellationToken cancellationToken)
    {
        if (!TryGet(await listGroups.ExecuteAsync(Id, cancellationToken), out (Entity Entity, IReadOnlyList<Group> Groups) listed, out IActionResult? failure))
        {
            return failure!;
        }

        Entity = listed.Entity;
        return Page();
    }
}

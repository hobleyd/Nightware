using Microsoft.AspNetCore.Mvc;

using Nightware.Application.Administration;
using Nightware.Application.Administration.Groups;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Admin.Pages.Entities.Groups;

/// <summary>
/// Confirmation page for deleting a group's team folder and its files. Opening it needs a fresh, strong sign-in;
/// GET never deletes; the use case re-checks every guard on POST.
/// </summary>
public sealed class DeleteFolderModel(IAdminActor actor, GetGroup getGroup, DeleteTeamFolder deleteFolder) : ConsolePageModel(actor)
{
    [BindProperty(SupportsGet = true)]
    public string Id { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string GroupId { get; set; } = string.Empty;

    public GroupDetail Detail { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!TryGet(await getGroup.ExecuteAsync(Id, GroupId, cancellationToken), out GroupDetail detail, out IActionResult? failure))
        {
            return failure!;
        }

        Detail = detail;
        Result allowed = deleteFolder.Check(detail.Group, typedName: null);
        return allowed.IsSuccess ? Page() : Fail(allowed.Error) ?? Page();
    }

    public async Task<IActionResult> OnPostAsync(string? confirmation, CancellationToken cancellationToken)
    {
        Result<Group> deleted = await deleteFolder.ExecuteAsync(Id, GroupId, confirmation, cancellationToken);
        if (deleted.IsSuccess)
        {
            Notice = "Team folder deleted with its files.";
            return RedirectToPage("/Entities/Groups/Detail", new { id = Id, groupId = GroupId });
        }

        if (Fail(deleted.Error) is { } outcome)
        {
            return outcome;
        }

        return TryGet(await getGroup.ExecuteAsync(Id, GroupId, cancellationToken), out GroupDetail detail, out IActionResult? failure)
            ? Show(detail)
            : failure!;
    }

    private Microsoft.AspNetCore.Mvc.RazorPages.PageResult Show(GroupDetail detail)
    {
        Detail = detail;
        return Page();
    }
}

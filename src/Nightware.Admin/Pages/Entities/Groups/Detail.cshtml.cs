using Microsoft.AspNetCore.Mvc;

using Nightware.Application.Administration;
using Nightware.Application.Administration.Groups;
using Nightware.Application.Administration.Users;
using Nightware.Domain.Kernel;

namespace Nightware.Admin.Pages.Entities.Groups;

/// <summary>One group: its state in each backend, its members, its settings, Repair and Delete.</summary>
public sealed class DetailModel(
    IAdminActor actor,
    GetGroup getGroup,
    ListUsers listUsers,
    UpdateGroup updateGroup,
    AddGroupMember addMember,
    RemoveGroupMember removeMember,
    RepairGroup repairGroup,
    DeleteGroup deleteGroup) : ConsolePageModel(actor)
{
    [BindProperty(SupportsGet = true)]
    public string Id { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string GroupId { get; set; } = string.Empty;

    [BindProperty]
    public GroupInput? Input { get; set; }

    public GroupDetail Detail { get; private set; } = null!;

    /// <summary>The entity's enabled users who are not in the group, to add.</summary>
    public IReadOnlyList<UserDetail> Candidates { get; private set; } = [];

    public Task<IActionResult> OnGetAsync(CancellationToken cancellationToken) => RenderAsync(cancellationToken);

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken cancellationToken) =>
        await AfterAsync(await updateGroup.ExecuteAsync(Id, GroupId, (Input ?? new GroupInput()).ToForm(), cancellationToken), "Group saved.", cancellationToken);

    public async Task<IActionResult> OnPostAddMemberAsync(string userId, CancellationToken cancellationToken) =>
        await AfterAsync(await addMember.ExecuteAsync(Id, GroupId, userId, cancellationToken), "Member added.", cancellationToken);

    public async Task<IActionResult> OnPostRemoveMemberAsync(string userId, CancellationToken cancellationToken) =>
        await AfterAsync(await removeMember.ExecuteAsync(Id, GroupId, userId, cancellationToken), "Member removed.", cancellationToken);

    public async Task<IActionResult> OnPostRepairAsync(CancellationToken cancellationToken) =>
        await AfterAsync(await repairGroup.ExecuteAsync(Id, GroupId, cancellationToken), "The list and the Nextcloud group were brought into line.", cancellationToken);

    public async Task<IActionResult> OnPostDeleteAsync(bool confirm, CancellationToken cancellationToken)
    {
        if (!confirm)
        {
            ModelState.AddModelError(string.Empty, "Tick the box to confirm deleting the group.");
            return await RenderAsync(cancellationToken);
        }

        Result deleted = await deleteGroup.ExecuteAsync(Id, GroupId, cancellationToken);
        if (deleted.IsSuccess)
        {
            Notice = "Group deleted.";
            return RedirectToPage("/Entities/Groups/Index", new { id = Id });
        }

        return Fail(deleted.Error) ?? await RenderAsync(cancellationToken);
    }

    private async Task<IActionResult> AfterAsync(Result<GroupSyncOutcome> result, string notice, CancellationToken cancellationToken)
    {
        if (result.IsSuccess)
        {
            Notice = string.Join(" ", result.Value.Notes.Prepend(notice));
            return RedirectToPage(new { id = Id, groupId = GroupId });
        }

        return Fail(result.Error) ?? await RenderAsync(cancellationToken);
    }

    private async Task<IActionResult> RenderAsync(CancellationToken cancellationToken)
    {
        if (!TryGet(await getGroup.ExecuteAsync(Id, GroupId, cancellationToken), out GroupDetail detail, out IActionResult? failure)
            || !TryGet(await listUsers.ExecuteAsync(Id, cancellationToken), out IReadOnlyList<UserDetail> users, out failure))
        {
            return failure!;
        }

        Detail = detail;
        var members = detail.Members.Select(static m => m.Id).ToHashSet(StringComparer.Ordinal);
        Candidates = [.. users.Where(u => u.User.Identity.Enabled && !members.Contains(u.User.Identity.Id)).OrderBy(static u => u.User.Identity.DisplayName, StringComparer.CurrentCultureIgnoreCase)];

        // A failed save shows what the admin typed; otherwise the form shows the group as it is.
        Input = ModelState.IsValid || Input is null ? GroupInput.From(detail.Group) : Input;
        return Page();
    }
}

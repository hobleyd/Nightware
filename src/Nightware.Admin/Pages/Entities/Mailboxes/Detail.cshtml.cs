using Microsoft.AspNetCore.Mvc;

using Nightware.Application.Administration;
using Nightware.Application.Administration.SharedMailboxes;
using Nightware.Application.Administration.Users;
using Nightware.Domain.Kernel;

namespace Nightware.Admin.Pages.Entities.Mailboxes;

/// <summary>One shared mailbox: its state on the mail server, its members, its settings and Repair.</summary>
public sealed class DetailModel(
    IAdminActor actor,
    GetSharedMailbox getMailbox,
    ListUsers listUsers,
    UpdateSharedMailbox updateMailbox,
    AddSharedMailboxMember addMember,
    RemoveSharedMailboxMember removeMember,
    RepairSharedMailbox repairMailbox,
    AdministrationOptions options) : ConsolePageModel(actor)
{
    [BindProperty(SupportsGet = true)]
    public string Id { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string MailboxId { get; set; } = string.Empty;

    [BindProperty]
    public MailboxInput? Input { get; set; }

    public SharedMailboxDetail Detail { get; private set; } = null!;

    /// <summary>The entity's enabled users who are not members, to add.</summary>
    public IReadOnlyList<UserDetail> Candidates { get; private set; } = [];

    /// <summary>Whether the actor may delete data of this entity (the delete page checks the rest).</summary>
    public bool MayDelete => AdminAuthorization.RequireMayDeleteData(Actor, Id, options).IsSuccess;

    public Task<IActionResult> OnGetAsync(CancellationToken cancellationToken) => RenderAsync(cancellationToken);

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken cancellationToken) =>
        await AfterAsync(await updateMailbox.ExecuteAsync(Id, MailboxId, (Input ?? new MailboxInput()).ToForm(), cancellationToken), "Shared mailbox saved.", cancellationToken);

    public async Task<IActionResult> OnPostAddMemberAsync(string userId, CancellationToken cancellationToken) =>
        await AfterAsync(await addMember.ExecuteAsync(Id, MailboxId, userId, cancellationToken), "Member added.", cancellationToken);

    public async Task<IActionResult> OnPostRemoveMemberAsync(string userId, CancellationToken cancellationToken) =>
        await AfterAsync(await removeMember.ExecuteAsync(Id, MailboxId, userId, cancellationToken), "Member removed.", cancellationToken);

    public async Task<IActionResult> OnPostRepairAsync(CancellationToken cancellationToken) =>
        await AfterAsync(await repairMailbox.ExecuteAsync(Id, MailboxId, cancellationToken), "The shared mailbox was brought into line.", cancellationToken);

    private async Task<IActionResult> AfterAsync(Result<SharedMailboxSyncOutcome> result, string notice, CancellationToken cancellationToken)
    {
        if (result.IsSuccess)
        {
            Notice = string.Join(" ", result.Value.Notes.Prepend(notice));
            return RedirectToPage(new { id = Id, mailboxId = MailboxId });
        }

        return Fail(result.Error) ?? await RenderAsync(cancellationToken);
    }

    private async Task<IActionResult> RenderAsync(CancellationToken cancellationToken)
    {
        if (!TryGet(await getMailbox.ExecuteAsync(Id, MailboxId, cancellationToken), out SharedMailboxDetail detail, out IActionResult? failure)
            || !TryGet(await listUsers.ExecuteAsync(Id, cancellationToken), out IReadOnlyList<UserDetail> users, out failure))
        {
            return failure!;
        }

        Detail = detail;
        var members = detail.Members.Select(static m => m.Id).ToHashSet(StringComparer.Ordinal);
        Candidates = [.. users.Where(u => u.User.Identity.Enabled && !members.Contains(u.User.Identity.Id)).OrderBy(static u => u.User.Identity.DisplayName, StringComparer.CurrentCultureIgnoreCase)];

        // A failed save shows what the admin typed; otherwise the form shows the mailbox as it is.
        Input = ModelState.IsValid || Input is null ? MailboxInput.From(detail.Mailbox) : Input;
        return Page();
    }
}

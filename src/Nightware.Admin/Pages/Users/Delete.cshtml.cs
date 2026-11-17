using Microsoft.AspNetCore.Mvc;

using Nightware.Application.Administration;
using Nightware.Application.Administration.Entities;
using Nightware.Application.Administration.Users;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Admin.Pages.Users;

/// <summary>
/// Confirmation page for deleting a disabled user's mail or Nextcloud data (ADR 0002 D10). Opening it
/// needs a fresh, strong sign-in; GET never deletes; the use case re-checks every guard on POST.
/// </summary>
public sealed class DeleteModel(
    IAdminActor actor,
    GetDeletionPreview preview,
    GetUser getUser,
    ListEntities entities,
    DeleteUserMail deleteMail,
    DeleteUserGroupwareData deleteGroupware,
    AdministrationOptions options,
    TimeProvider time) : ConsolePageModel(actor)
{
    [BindProperty(SupportsGet = true)]
    public string Id { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public DeletionTarget Target { get; set; }

    public DeletionPreview Preview { get; private set; } = null!;

    public DeletionOutcome? Outcome { get; private set; }

    /// <summary>The user's entity, for the breadcrumb; null when the user could not be read.</summary>
    public string? EntityId { get; private set; }

    /// <summary>The entity's name, for the breadcrumb; null when it cannot be read.</summary>
    public string? EntityName { get; private set; }

    /// <summary>What is deleted, in words: "mail" or "files &amp; calendar data".</summary>
    public string What => Target == DeletionTarget.Mail ? "mail" : "files & calendar data";

    /// <summary>Whose data, read when the preview refuses, so the refusal can name them; null when unknown.</summary>
    public UserDetail? Subject { get; private set; }

    /// <summary>The refusal is an offboarding that has not finished, which the page can offer to finish.</summary>
    public bool MayFinishOffboarding => Subject is { User.Identity.Enabled: false } && OffboardUser.IsIncomplete(Subject);

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        Result fresh = AdminAuthorization.RequireFreshAuthentication(Actor, time.GetUtcNow(), options);
        if (!fresh.IsSuccess)
        {
            return Fail(fresh.Error)!;
        }

        return await RenderAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostAsync(string? confirmation, CancellationToken cancellationToken)
    {
        Result<DeletionOutcome> deleted = Target == DeletionTarget.Mail
            ? await deleteMail.ExecuteAsync(Id, confirmation, cancellationToken)
            : await deleteGroupware.ExecuteAsync(Id, confirmation, cancellationToken);
        if (deleted.IsSuccess)
        {
            Notice = deleted.Value.AlreadyAbsent
                ? "Nothing to delete: it was already gone."
                : Target == DeletionTarget.Mail ? "The mailbox and its mail were deleted. The server reclaims the space shortly." : "The Nextcloud user and their data were deleted.";
            return RedirectToPage("/Users/Detail", new { id = Id });
        }

        return Fail(deleted.Error) ?? await RenderAsync(cancellationToken);
    }

    private async Task<IActionResult> RenderAsync(CancellationToken cancellationToken)
    {
        if (!TryGet(await preview.ExecuteAsync(Id, Target, cancellationToken), out DeletionPreview loaded, out IActionResult? failure))
        {
            if (Unread is { Kind: ErrorKind.Validation or ErrorKind.Conflict } && await getUser.ExecuteAsync(Id, cancellationToken) is { IsSuccess: true } user)
            {
                Subject = user.Value;
                await NameEntityAsync(user.Value.User.EntityId, cancellationToken);
            }

            return failure!;
        }

        Preview = loaded;
        await NameEntityAsync(loaded.User.EntityId, cancellationToken);
        return Page();
    }

    private async Task NameEntityAsync(string? entityId, CancellationToken cancellationToken)
    {
        EntityId = entityId;
        if (entityId is not null && await entities.ExecuteAsync(cancellationToken) is { IsSuccess: true } manageable)
        {
            EntityName = manageable.Value.FirstOrDefault(e => e.Id == entityId)?.Name;
        }
    }
}

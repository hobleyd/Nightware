using Microsoft.AspNetCore.Mvc;

using Nightware.Application.Administration;
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
            return failure!;
        }

        Preview = loaded;
        return Page();
    }
}

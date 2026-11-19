using Microsoft.AspNetCore.Mvc;

using Nightware.Application.Administration;
using Nightware.Application.Administration.SharedMailboxes;
using Nightware.Domain.Kernel;

namespace Nightware.Admin.Pages.Entities.Mailboxes;

/// <summary>
/// Confirmation page for deleting a shared mailbox and its mail. Opening it needs a fresh, strong sign-in; GET never
/// deletes; the use case re-checks every guard on POST.
/// </summary>
public sealed class DeleteModel(IAdminActor actor, GetSharedMailbox getMailbox, DeleteSharedMailbox deleteMailbox) : ConsolePageModel(actor)
{
    [BindProperty(SupportsGet = true)]
    public string Id { get; set; } = string.Empty;

    [BindProperty(SupportsGet = true)]
    public string MailboxId { get; set; } = string.Empty;

    public SharedMailboxDetail Detail { get; private set; } = null!;

    /// <summary>Bytes the mailbox holds; null when not known.</summary>
    public long? Usage { get; private set; }

    public bool Allowed { get; private set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!TryGet(await getMailbox.ExecuteAsync(Id, MailboxId, cancellationToken), out SharedMailboxDetail detail, out IActionResult? failure))
        {
            return failure!;
        }

        Result allowed = deleteMailbox.Check(detail.Mailbox, typedAddress: null);
        if (!allowed.IsSuccess)
        {
            return Fail(allowed.Error) ?? await ShowAsync(detail, cancellationToken);
        }

        Allowed = true;
        return await ShowAsync(detail, cancellationToken);
    }

    public async Task<IActionResult> OnPostAsync(string? confirmation, CancellationToken cancellationToken)
    {
        Result deleted = await deleteMailbox.ExecuteAsync(Id, MailboxId, confirmation, cancellationToken);
        if (deleted.IsSuccess)
        {
            Notice = "Shared mailbox deleted with its mail.";
            return RedirectToPage("/Entities/Mailboxes/Index", new { id = Id });
        }

        if (Fail(deleted.Error) is { } outcome)
        {
            return outcome;
        }

        if (!TryGet(await getMailbox.ExecuteAsync(Id, MailboxId, cancellationToken), out SharedMailboxDetail detail, out IActionResult? failure))
        {
            return failure!;
        }

        Allowed = deleteMailbox.Check(detail.Mailbox, typedAddress: null).IsSuccess;
        return await ShowAsync(detail, cancellationToken);
    }

    private async Task<IActionResult> ShowAsync(SharedMailboxDetail detail, CancellationToken cancellationToken)
    {
        Detail = detail;
        Result<long?> usage = Allowed ? await deleteMailbox.GetUsageAsync(detail.Mailbox, cancellationToken) : Result<long?>.Success(null);
        Usage = usage.IsSuccess ? usage.Value : null;
        return Page();
    }
}

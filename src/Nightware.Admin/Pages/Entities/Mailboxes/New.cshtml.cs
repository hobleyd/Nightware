using Microsoft.AspNetCore.Mvc;

using Nightware.Application.Administration;
using Nightware.Application.Administration.SharedMailboxes;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Admin.Pages.Entities.Mailboxes;

/// <summary>Creates a shared mailbox of an entity; the mail server's mailbox is built straight away.</summary>
public sealed class NewModel(IAdminActor actor, ListSharedMailboxes listMailboxes, CreateSharedMailbox createMailbox) : ConsolePageModel(actor)
{
    [BindProperty(SupportsGet = true)]
    public string Id { get; set; } = string.Empty;

    [BindProperty]
    public MailboxInput Input { get; set; } = new();

    public Entity Entity { get; private set; } = null!;

    public Task<IActionResult> OnGetAsync(CancellationToken cancellationToken) => RenderAsync(cancellationToken);

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        Result<SharedMailboxSyncOutcome> created = await createMailbox.ExecuteAsync(Id, Input.ToForm(), cancellationToken);
        if (created.IsSuccess)
        {
            Notice = string.Join(" ", created.Value.Notes.Prepend("Shared mailbox created."));
            return RedirectToPage("/Entities/Mailboxes/Detail", new { id = Id, mailboxId = created.Value.Mailbox.Id });
        }

        return Fail(created.Error) ?? await RenderAsync(cancellationToken);
    }

    private async Task<IActionResult> RenderAsync(CancellationToken cancellationToken)
    {
        if (!TryGet(await listMailboxes.ExecuteAsync(Id, cancellationToken), out (Entity Entity, IReadOnlyList<SharedMailbox> Mailboxes) listed, out IActionResult? failure))
        {
            return failure!;
        }

        Entity = listed.Entity;
        return Page();
    }
}

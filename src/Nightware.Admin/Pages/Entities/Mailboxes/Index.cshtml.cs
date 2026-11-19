using Microsoft.AspNetCore.Mvc;

using Nightware.Application.Administration;
using Nightware.Application.Administration.SharedMailboxes;
using Nightware.Domain.Administration;

namespace Nightware.Admin.Pages.Entities.Mailboxes;

/// <summary>An entity's shared mailboxes (phase 10), by name.</summary>
public sealed class IndexModel(IAdminActor actor, ListSharedMailboxes listMailboxes) : ConsolePageModel(actor)
{
    [BindProperty(SupportsGet = true)]
    public string Id { get; set; } = string.Empty;

    public Entity Entity { get; private set; } = null!;

    public IReadOnlyList<SharedMailbox> Mailboxes { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!TryGet(await listMailboxes.ExecuteAsync(Id, cancellationToken), out (Entity Entity, IReadOnlyList<SharedMailbox> Mailboxes) listed, out IActionResult? failure))
        {
            return failure!;
        }

        (Entity, Mailboxes) = listed;
        return Page();
    }
}

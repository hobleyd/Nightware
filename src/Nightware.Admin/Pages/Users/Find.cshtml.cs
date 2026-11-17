using Microsoft.AspNetCore.Mvc;

using Nightware.Application.Administration;
using Nightware.Application.Administration.Users;

namespace Nightware.Admin.Pages.Users;

/// <summary>The header's "find a user": one match opens that user, several are listed with their entity.</summary>
public sealed class FindModel(IAdminActor actor, FindUsers find) : ConsolePageModel(actor)
{
    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    public IReadOnlyList<FoundUser> Found { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!TryGet(await find.ExecuteAsync(Q, cancellationToken), out IReadOnlyList<FoundUser> found, out IActionResult? failure))
        {
            return failure!;
        }

        if (found.Count == 1)
        {
            return RedirectToPage("/Users/Detail", new { id = found[0].Identity.Id });
        }

        Found = found;
        return Page();
    }
}

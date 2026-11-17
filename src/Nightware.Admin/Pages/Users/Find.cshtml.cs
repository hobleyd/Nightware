using Microsoft.AspNetCore.Mvc;

using Nightware.Application.Administration;
using Nightware.Application.Administration.Users;

namespace Nightware.Admin.Pages.Users;

/// <summary>The header's "find a user": one match opens that user, several are listed with their entity.</summary>
public sealed class FindModel(IAdminActor actor, FindUsers find) : ConsolePageModel(actor)
{
    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    public FoundUsers Found { get; private set; } = new([], false);

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (!TryGet(await find.ExecuteAsync(Q, cancellationToken), out FoundUsers found, out IActionResult? failure))
        {
            return failure!;
        }

        if (found.Users.Count == 1)
        {
            return RedirectToPage("/Users/Detail", new { id = found.Users[0].User.User.Identity.Id });
        }

        Found = found;
        return Page();
    }
}

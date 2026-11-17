using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Admin.Pages;

/// <summary>
/// What every console page shares: the actor (for deciding what to <em>show</em>; the use cases decide what
/// is <em>allowed</em>) and one translation of <see cref="Error"/> into a page outcome.
/// </summary>
public abstract class ConsolePageModel(IAdminActor actor) : PageModel
{
    public AdminActor Actor => actor.Current;

    /// <summary>A one-off confirmation shown after a post-redirect-get.</summary>
    [TempData]
    public string? Notice { get; set; }

    /// <summary>
    /// Not found and forbidden become their status pages; a stale sign-in goes back to Keycloak; anything
    /// else is shown on the page (returns null so the handler can re-render).
    /// </summary>
    protected IActionResult? Fail(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        switch (error.Kind)
        {
            case ErrorKind.NotFound when error.Target is null:
                return NotFound();
            case ErrorKind.Forbidden:
                return StatusCode(StatusCodes.Status403Forbidden);
            case ErrorKind.Unauthorized when error.Code == "reauthenticationRequired":
                if (!HttpMethods.IsGet(Request.Method))
                {
                    // Survives the round trip to Keycloak, so the admin knows nothing happened yet.
                    Notice = "You signed in again. Nothing was changed: repeat the action to complete it.";
                }

                return RedirectToPage("/Account/Reauthenticate", new { returnUrl = ReturnUrl() });
            default:
                ModelState.AddModelError(error.Target ?? string.Empty, error.Message);
                return null;
        }
    }

    /// <summary>
    /// Where to come back to after signing in again: this page, without a POST's <c>handler</c>, which would
    /// otherwise turn the return into a GET of a handler that does not exist.
    /// </summary>
    private string ReturnUrl()
    {
        QueryString query = QueryString.Create(Request.Query.Where(static q => !string.Equals(q.Key, "handler", StringComparison.OrdinalIgnoreCase)));
        return Request.PathBase + Request.Path + query;
    }

    /// <summary>The value, or the page outcome for its error.</summary>
    protected bool TryGet<T>(Result<T> result, out T value, out IActionResult? failure)
    {
        if (result.IsSuccess)
        {
            value = result.Value;
            failure = null;
            return true;
        }

        value = default!;
        failure = Fail(result.Error) ?? Page();
        return false;
    }
}

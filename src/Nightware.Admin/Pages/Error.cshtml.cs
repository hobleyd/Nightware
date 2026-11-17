using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Nightware.Admin.Pages;

/// <summary>Status and exception pages. Never shows exception detail.</summary>
[AllowAnonymous]
[IgnoreAntiforgeryToken]
public sealed class ErrorModel : PageModel
{
    public string Title { get; private set; } = "Something went wrong";

    public string Message { get; private set; } = "The console could not complete the request. It has been logged.";

    public void OnGet(int? status) => Describe(status);

    public void OnPost(int? status) => Describe(status);

    private void Describe(int? status)
    {
        (Title, Message) = status switch
        {
            StatusCodes.Status403Forbidden => ("Not allowed", "You are not allowed to do that."),
            StatusCodes.Status404NotFound => ("Not found", "That page or record does not exist, or is outside your scope."),
            StatusCodes.Status400BadRequest => ("Bad request", "The form had expired or was not sent by the console. Go back, reload and try again."),
            _ => (Title, Message),
        };
    }
}

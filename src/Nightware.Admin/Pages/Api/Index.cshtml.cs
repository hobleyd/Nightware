using Microsoft.AspNetCore.Mvc;

using Nightware.Application.Administration;
using Nightware.Hosting.Http;

namespace Nightware.Admin.Pages.Api;

/// <summary>
/// The Graph API reference for third parties integrating with Nightware: Swagger UI over the OpenAPI document of every
/// operation the façade implements. Any signed-in administrator may read it.
/// </summary>
public sealed class IndexModel(IAdminActor actor) : ConsolePageModel(actor)
{
    /// <summary>
    /// The console's policy, widened for this page only: its own scripts (Swagger UI, never inline), the inline style
    /// attributes Swagger UI writes, its data: icons, and calls to this origin (the document, and "Try it out" against
    /// /graph with the bearer token the reader pastes).
    /// </summary>
    internal const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";

    public IActionResult OnGet()
    {
        HttpContext.Items[SecurityHeadersMiddleware.ContentSecurityPolicyItem] = ContentSecurityPolicy;
        return Page();
    }
}

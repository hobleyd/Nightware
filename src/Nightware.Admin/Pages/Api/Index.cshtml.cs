using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

using Nightware.Application.Administration;
using Nightware.Hosting.Http;

namespace Nightware.Admin.Pages.Api;

/// <summary>
/// The Graph API reference for third parties integrating with Nightware: Swagger UI over the OpenAPI document of every
/// operation the façade implements. Any signed-in administrator may read it.
/// </summary>
public sealed class IndexModel(IAdminActor actor, IOptions<SecurityHeadersOptions> headers) : ConsolePageModel(actor)
{
    public IActionResult OnGet()
    {
        HttpContext.Items[SecurityHeadersMiddleware.ContentSecurityPolicyItem] = Widen(headers.Value.ContentSecurityPolicy);
        return Page();
    }

    /// <summary>
    /// The console's policy, widened for this page only: its own scripts (Swagger UI, never inline), the inline style
    /// attributes Swagger UI writes, its data: icons, and calls to this origin (the document, and "Try it out" against
    /// /graph with the bearer token the reader pastes). Everything else, form targets included, stays the console's.
    /// </summary>
    internal static string Widen(string policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var directives = policy.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(d => !d.StartsWith("script-src ", StringComparison.Ordinal) && !d.StartsWith("connect-src ", StringComparison.Ordinal))
            .Select(static d => d.Split(' ')[0] switch
            {
                "style-src" => "style-src 'self' 'unsafe-inline'",
                "img-src" => "img-src 'self' data:",
                _ => d,
            })
            .ToList();
        directives.InsertRange(1, ["script-src 'self'", "connect-src 'self'"]);
        return string.Join("; ", directives);
    }
}

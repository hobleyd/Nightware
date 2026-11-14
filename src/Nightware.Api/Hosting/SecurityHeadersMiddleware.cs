using System.Globalization;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Nightware.Api.Hosting;

/// <summary>Response-header hardening (configuration section <c>SecurityHeaders</c>).</summary>
public sealed class SecurityHeadersOptions
{
    public const string SectionName = "SecurityHeaders";

    /// <summary>
    /// <c>Strict-Transport-Security</c> max-age, sent only on requests that arrived over HTTPS (as
    /// reported by the trusted proxy). Zero disables the header, e.g. when Caddy already sets it.
    /// </summary>
    public TimeSpan HstsMaxAge { get; set; } = TimeSpan.FromDays(365);
}

/// <summary>
/// Adds the headers every façade response carries. The API serves JSON and raw attachment bytes
/// (<c>$value</c>), never pages, so the policy is "render nothing": an HTML attachment opened
/// directly in a browser cannot run script or be framed, and mail data is never cached by a
/// shared or browser cache unless an endpoint deliberately sets its own <c>Cache-Control</c>.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next, IOptions<SecurityHeadersOptions> options)
{
    public const string ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'; sandbox";

    private readonly string? _hsts = options.Value.HstsMaxAge > TimeSpan.Zero
        ? string.Create(CultureInfo.InvariantCulture, $"max-age={(long)options.Value.HstsMaxAge.TotalSeconds}")
        : null;

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.OnStarting(static state =>
        {
            var (ctx, hsts) = ((HttpContext, string?))state;
            var headers = ctx.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers[HeaderNames.ContentSecurityPolicy] = ContentSecurityPolicy;
            headers["Referrer-Policy"] = "no-referrer";
            if (string.IsNullOrEmpty(headers.CacheControl))
            {
                headers.CacheControl = "no-store";
            }

            if (hsts is not null && ctx.Request.IsHttps)
            {
                headers.StrictTransportSecurity = hsts;
            }

            return Task.CompletedTask;
        }, (context, _hsts));
        return next(context);
    }
}

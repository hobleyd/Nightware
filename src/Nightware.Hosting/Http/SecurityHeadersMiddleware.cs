using System.Globalization;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Nightware.Hosting.Http;

/// <summary>Response-header hardening (configuration section <c>SecurityHeaders</c>).</summary>
public sealed class SecurityHeadersOptions
{
    public const string SectionName = "SecurityHeaders";

    /// <summary>
    /// The Graph façade's policy: it serves JSON and raw attachment bytes, never pages, so nothing may
    /// render, run script or be framed.
    /// </summary>
    public const string DefaultContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'; sandbox";

    /// <summary>
    /// <c>Strict-Transport-Security</c> max-age, sent only on requests that arrived over HTTPS (as
    /// reported by the trusted proxy). Zero disables the header, e.g. when Caddy already sets it.
    /// </summary>
    public TimeSpan HstsMaxAge { get; set; } = TimeSpan.FromDays(365);

    /// <summary>
    /// The <c>Content-Security-Policy</c> sent on every response. A host that serves pages (the admin
    /// console) sets its own; the default is <see cref="DefaultContentSecurityPolicy"/>.
    /// </summary>
    public string ContentSecurityPolicy { get; set; } = DefaultContentSecurityPolicy;
}

internal sealed class SecurityHeadersOptionsValidator : IValidateOptions<SecurityHeadersOptions>
{
    public ValidateOptionsResult Validate(string? name, SecurityHeadersOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return string.IsNullOrWhiteSpace(options.ContentSecurityPolicy)
            ? ValidateOptionsResult.Fail("SecurityHeaders:ContentSecurityPolicy must not be empty.")
            : ValidateOptionsResult.Success;
    }
}

/// <summary>
/// Adds the headers every response carries. The Graph façade serves JSON and raw attachment bytes
/// (<c>$value</c>), never pages, so its policy (the default) is "render nothing": an HTML attachment
/// opened directly in a browser cannot run script or be framed, and mail data is never cached by a
/// shared or browser cache unless an endpoint deliberately sets its own <c>Cache-Control</c>. The
/// Content-Security-Policy comes from <see cref="SecurityHeadersOptions.ContentSecurityPolicy"/>.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next, IOptions<SecurityHeadersOptions> options)
{
    private readonly string _contentSecurityPolicy = options.Value.ContentSecurityPolicy;

    private readonly string? _hsts = options.Value.HstsMaxAge > TimeSpan.Zero
        ? string.Create(CultureInfo.InvariantCulture, $"max-age={(long)options.Value.HstsMaxAge.TotalSeconds}")
        : null;

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Response.OnStarting(static state =>
        {
            var (ctx, csp, hsts) = ((HttpContext, string, string?))state;
            var headers = ctx.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers[HeaderNames.ContentSecurityPolicy] = csp;
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
        }, (context, _contentSecurityPolicy, _hsts));
        return next(context);
    }
}

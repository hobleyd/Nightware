using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Nightware.Api.Auth;

/// <summary>
/// One kind of self-authorising URL (ADR 0001 §39), and the endpoint metadata that marks the route it
/// authorises: the key purpose its credential is sealed under, the one method it serves, how long it may
/// live, and which request values it is bound to.
/// </summary>
/// <remarks>
/// <para>The sealed credential always rides in the <see cref="AuthTokenParameter"/> query parameter, never in
/// the path: paths are logged and span-tagged, queries are redacted (<c>LogRedaction</c>).</para>
/// <para>The binding is read from the <b>matched</b> route's values (routing runs before
/// <see cref="SelfAuthorisingUrlMiddleware"/>), plus whatever query options the kind names, so a URL opens
/// only for the request it was minted for. The minting side passes the same values to
/// <see cref="SelfAuthorisingUrlSealer.Seal"/>; the method is always the first bound field.</para>
/// </remarks>
/// <param name="purpose">Names the kind for key derivation (e.g. <c>nightware/todo-upload-url/v1</c>); keys never cross purposes.</param>
/// <param name="method">The one HTTP method the URL authorises.</param>
/// <param name="lifetime">The longest a URL of this kind lives; a ticket never outlives the access token inside it either.</param>
/// <param name="bindingOf">The bound values a request names, or null when it names none (wrong shape, bad option).</param>
/// <param name="urlOnly">
/// True when the URL is the only credential the route accepts: an <c>Authorization</c> header is ignored, so a
/// caller cannot reach the route with an ordinary bearer and skip what minting the URL checked.
/// </param>
public sealed class SelfAuthorisingUrl(string purpose, string method, TimeSpan lifetime, Func<HttpRequest, IReadOnlyList<string>?> bindingOf, bool urlOnly = false)
{
    public const string AuthTokenParameter = "authtoken";

    /// <summary>Kestrel's default request-line limit; a minted URL must stay well inside it.</summary>
    public const int MaxRequestLine = 8192;

    public string Purpose { get; } = string.IsNullOrWhiteSpace(purpose) ? throw new ArgumentException("A purpose is required.", nameof(purpose)) : purpose;

    public string Method { get; } = method;

    public TimeSpan Lifetime { get; } = lifetime;

    public bool UrlOnly { get; } = urlOnly;

    /// <summary>The bound values <paramref name="request"/> names, or null.</summary>
    public IReadOnlyList<string>? BindingOf(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return bindingOf(request);
    }

    /// <summary>False when the URL is too long for the server to read back (an oversized access token).</summary>
    public static bool FitsRequestLine(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return url.Length <= MaxRequestLine - 64;
    }

    /// <summary>
    /// A <c>302</c> to a minted URL. Not <see cref="Results.Redirect(string, bool, bool)"/>: that logs its
    /// destination — query and credential included — at Information.
    /// </summary>
    public static IResult Redirect(string url) => new SilentRedirect(url);

    /// <summary>The route value <paramref name="name"/> as a non-empty string, or null.</summary>
    public static string? RouteValue(HttpRequest request, string name)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.RouteValues[name] is string { Length: > 0 } value ? value : null;
    }

    private sealed class SilentRedirect(string url) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            httpContext.Response.StatusCode = StatusCodes.Status302Found;
            httpContext.Response.Headers[HeaderNames.Location] = url;
            return Task.CompletedTask;
        }
    }
}

public static class SelfAuthorisingUrlEndpointExtensions
{
    /// <summary>Marks the route as reachable through URLs of <paramref name="kind"/> (see <see cref="SelfAuthorisingUrlMiddleware"/>).</summary>
    public static TBuilder AuthorisedBy<TBuilder>(this TBuilder builder, SelfAuthorisingUrl kind)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(kind);
        return builder.WithMetadata(kind);
    }
}

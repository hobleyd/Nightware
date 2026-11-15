using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;

using Nightware.Api.Auth;
using Nightware.Api.Graph;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Features.Tasks.Upload;

/// <summary>
/// Authenticates the anonymous upload <c>PUT</c> from its URL. The ADR 0001 §39 design does this in
/// <see cref="SelfAuthorisingUrlMiddleware"/>, but a feature cannot register an
/// <see cref="IRequestCredentialSource"/> there (only the frozen composition root adds services),
/// so the route is anonymous and this filter performs the same step for it: recover the token, set
/// <c>Authorization</c>, and run the normal (default) authentication scheme on it — the same JWT
/// validation as any other request. A request the middleware (or a
/// real bearer) already authenticated passes straight through.
/// </summary>
internal sealed class UploadUrlAuthenticationFilter(IRequestCredentialSource source) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;
        if (http.User.Identity?.IsAuthenticated != true)
        {
            if (!source.TryResolve(http.Request, out var token))
            {
                return Unauthorized();
            }

            http.Request.Headers[HeaderNames.Authorization] = $"Bearer {token}";
            var result = await AuthenticateAfreshAsync(http).ConfigureAwait(false);
            if (result is not { Succeeded: true, Principal: { } principal })
            {
                return Unauthorized();
            }

            http.User = principal;
        }

        return await next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the default authentication scheme on a new handler instance. The request's own handler
    /// already ran (with no header) and caches that outcome for the rest of the request, so asking it
    /// again would only repeat "no result".
    /// </summary>
    private static async Task<AuthenticateResult?> AuthenticateAfreshAsync(HttpContext http)
    {
        var schemes = http.RequestServices.GetRequiredService<IAuthenticationSchemeProvider>();
        if (await schemes.GetDefaultAuthenticateSchemeAsync().ConfigureAwait(false) is not { } scheme
            || ActivatorUtilities.CreateInstance(http.RequestServices, scheme.HandlerType) is not IAuthenticationHandler handler)
        {
            return null;
        }

        await handler.InitializeAsync(scheme, http).ConfigureAwait(false);
        return await handler.AuthenticateAsync().ConfigureAwait(false);
    }

    private static IResult Unauthorized() =>
        GraphResults.Error(new Error(ErrorKind.Unauthorized, "InvalidAuthenticationToken", "The upload URL is invalid or has expired."));
}

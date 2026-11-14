using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Nightware.Api.Auth;

/// <summary>
/// Runs before authentication. When a request has no <c>Authorization</c> header, asks each
/// <see cref="IRequestCredentialSource"/> for a bearer token and, if one answers, sets the header
/// so JWT validation, <see cref="Application.Common.Identity.IUserContext"/> and backend token
/// exchange proceed exactly as for an ordinary call. A recovered token gets no special trust: an
/// expired or forged one fails validation like any other.
/// </summary>
internal sealed class SelfAuthorisingUrlMiddleware(RequestDelegate next, IEnumerable<IRequestCredentialSource> sources)
{
    private readonly IRequestCredentialSource[] _sources = [.. sources];

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var headers = context.Request.Headers;
        if (_sources.Length > 0 && string.IsNullOrEmpty(headers[HeaderNames.Authorization]))
        {
            foreach (var source in _sources)
            {
                if (source.TryResolve(context.Request, out var token) && !string.IsNullOrWhiteSpace(token))
                {
                    headers[HeaderNames.Authorization] = $"Bearer {token}";
                    break;
                }
            }
        }

        return next(context);
    }
}

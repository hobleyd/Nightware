using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Nightware.Api.Auth;

/// <summary>
/// Runs after routing and before authentication, and only for a route marked with <see cref="SelfAuthorisingUrl"/>
/// metadata. When the request has no <c>Authorization</c> header — or the route takes no other credential
/// (<see cref="SelfAuthorisingUrl.UrlOnly"/>), in which case any header is dropped — asks each
/// <see cref="IRequestCredentialSource"/> for a ticket and, if one answers, sets the header from it and keeps the
/// ticket as a request feature, so JWT validation, <see cref="Application.Common.Identity.IUserContext"/> and backend
/// token exchange proceed exactly as for an ordinary call. A recovered token gets no special trust: an expired or
/// forged one fails validation like any other.
/// </summary>
internal sealed class SelfAuthorisingUrlMiddleware(RequestDelegate next, IEnumerable<IRequestCredentialSource> sources)
{
    private readonly IRequestCredentialSource[] _sources = [.. sources];

    public Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.GetEndpoint()?.Metadata.GetMetadata<SelfAuthorisingUrl>() is not { } kind)
        {
            return next(context);
        }

        var headers = context.Request.Headers;
        if (kind.UrlOnly)
        {
            headers.Remove(HeaderNames.Authorization);
        }

        if (string.IsNullOrEmpty(headers[HeaderNames.Authorization]))
        {
            foreach (var source in _sources)
            {
                if (source.TryResolve(context.Request, out var ticket) && !string.IsNullOrWhiteSpace(ticket.BearerToken))
                {
                    headers[HeaderNames.Authorization] = $"Bearer {ticket.BearerToken}";
                    context.Features.Set(ticket);
                    break;
                }
            }
        }

        return next(context);
    }
}

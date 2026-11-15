using System.Diagnostics.CodeAnalysis;

using Microsoft.AspNetCore.Http;

namespace Nightware.Api.Auth;

/// <summary>
/// Opens the ticket of any self-authorising URL (ADR 0001 §39): only for the method of the matched route's
/// <see cref="SelfAuthorisingUrl"/> metadata, and only when the single sealed <c>authtoken</c> opens for exactly
/// the values that route matched.
/// </summary>
/// <remarks>
/// A body of the wrong length is not an authentication failure — NightMail treats 401 as "sign in again" — so
/// whatever the ticket says about the body is the endpoint's to enforce, with 400/413.
/// </remarks>
internal sealed class SelfAuthorisingUrlCredentialSource(SelfAuthorisingUrlSealer sealer) : IRequestCredentialSource
{
    public bool TryResolve(HttpRequest request, [NotNullWhen(true)] out SealedTicket? ticket)
    {
        ArgumentNullException.ThrowIfNull(request);
        ticket = null;
        return request.HttpContext.GetEndpoint()?.Metadata.GetMetadata<SelfAuthorisingUrl>() is { } kind
               && HttpMethods.Equals(request.Method, kind.Method)
               && request.Query[SelfAuthorisingUrl.AuthTokenParameter] is [{ } sealedValue]
               && kind.BindingOf(request) is { } boundTo
               && sealer.TryOpen(kind, sealedValue, boundTo, out ticket);
    }
}

using System.Diagnostics.CodeAnalysis;

using Microsoft.AspNetCore.Http;

namespace Nightware.Api.Auth;

/// <summary>
/// Recovers the caller's credential from a request that cannot carry an <c>Authorization</c> header —
/// Graph "self-authorising" URLs such as a To Do <c>uploadUrl</c>, which NightMail PUTs without a bearer.
/// The façade's one source (<see cref="SelfAuthorisingUrlCredentialSource"/>) opens the ticket sealed into
/// a URL the façade minted.
/// </summary>
public interface IRequestCredentialSource
{
    /// <returns>True when this source recognises the request and recovered a ticket.</returns>
    bool TryResolve(HttpRequest request, [NotNullWhen(true)] out SealedTicket? ticket);
}

using Microsoft.AspNetCore.Http;

namespace Nightware.Api.Auth;

/// <summary>
/// Recovers the caller's bearer token from a request that cannot carry an <c>Authorization</c>
/// header — Graph "self-authorising" URLs such as a To Do <c>uploadUrl</c>, which NightMail
/// PUTs without a bearer. A source typically decrypts a token embedded in the URL it minted.
/// </summary>
public interface IRequestCredentialSource
{
    /// <returns>True when this source recognises the request and recovered a bearer token.</returns>
    bool TryResolve(HttpRequest request, out string bearerToken);
}

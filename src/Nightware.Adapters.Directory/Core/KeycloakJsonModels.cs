using System.Text.Json.Serialization;

namespace Nightware.Adapters.Directory.Core;

/// <summary>Successful token-endpoint response (RFC 6749 §5.1, RFC 8693 §2.2.1).</summary>
internal sealed record TokenEndpointResponse(
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("expires_in")] int? ExpiresIn,
    [property: JsonPropertyName("token_type")] string? TokenType,
    [property: JsonPropertyName("issued_token_type")] string? IssuedTokenType);

/// <summary>Error response (RFC 6749 §5.2).</summary>
internal sealed record OAuthErrorResponse(
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("error_description")] string? ErrorDescription);

/// <summary>The part of the OIDC discovery document the health check reads.</summary>
internal sealed record DiscoveryDocument(
    [property: JsonPropertyName("issuer")] string? Issuer,
    [property: JsonPropertyName("token_endpoint")] string? TokenEndpoint);

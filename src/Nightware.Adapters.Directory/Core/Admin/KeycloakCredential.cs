using System.Text.Json.Serialization;

namespace Nightware.Adapters.Directory.Core.Admin;

/// <summary>The fields of Keycloak's <c>CredentialRepresentation</c> the console reads (never the secret data).</summary>
public sealed record KeycloakCredential
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary><c>password</c>, <c>otp</c>, <c>webauthn</c> (a passkey or security key), …</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>Milliseconds since the Unix epoch.</summary>
    [JsonPropertyName("createdDate")]
    public long? CreatedDate { get; init; }
}

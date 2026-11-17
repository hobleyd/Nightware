using System.Text.Json.Serialization;

namespace Nightware.Adapters.Directory.Core.Admin;

/// <summary>Body of <c>POST /users</c>: a realm user created by the console (ADR 0002 D5).</summary>
public sealed record KeycloakNewUser
{
    [JsonPropertyName("username")]
    public required string Username { get; init; }

    [JsonPropertyName("email")]
    public required string Email { get; init; }

    [JsonPropertyName("firstName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FirstName { get; init; }

    [JsonPropertyName("lastName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastName { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    /// <summary>Admin-entered addresses are marked verified (ADR 0002 D5).</summary>
    [JsonPropertyName("emailVerified")]
    public bool EmailVerified { get; init; } = true;

    /// <summary>Keycloak required-action aliases, e.g. <c>UPDATE_PASSWORD</c>, <c>CONFIGURE_TOTP</c>.</summary>
    [JsonPropertyName("requiredActions")]
    public IReadOnlyList<string> RequiredActions { get; init; } = [];
}

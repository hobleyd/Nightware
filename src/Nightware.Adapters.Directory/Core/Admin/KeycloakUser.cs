using System.Text.Json.Serialization;

namespace Nightware.Adapters.Directory.Core.Admin;

/// <summary>
/// The fields of Keycloak's <c>UserRepresentation</c> the façade reads
/// (https://www.keycloak.org/docs-api/latest/rest-api/index.html#UserRepresentation). Unmodelled
/// fields are preserved on update because <see cref="IKeycloakAdminClient.UpdateUserAttributesAsync"/>
/// round-trips the raw JSON.
/// </summary>
public sealed record KeycloakUser
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("username")]
    public string? Username { get; init; }

    [JsonPropertyName("email")]
    public string? Email { get; init; }

    [JsonPropertyName("firstName")]
    public string? FirstName { get; init; }

    [JsonPropertyName("lastName")]
    public string? LastName { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("emailVerified")]
    public bool EmailVerified { get; init; }

    /// <summary>Milliseconds since the Unix epoch.</summary>
    [JsonPropertyName("createdTimestamp")]
    public long? CreatedTimestamp { get; init; }

    /// <summary>
    /// Custom attributes; Keycloak stores every attribute as a list of strings. Absent when the user
    /// has none or when the list was requested with <c>briefRepresentation=true</c>.
    /// </summary>
    [JsonPropertyName("attributes")]
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? Attributes { get; init; }

    /// <summary>First value of attribute <paramref name="name"/>, or null.</summary>
    public string? Attribute(string name) =>
        Attributes is not null && Attributes.TryGetValue(name, out IReadOnlyList<string>? values) && values.Count > 0 ? values[0] : null;
}

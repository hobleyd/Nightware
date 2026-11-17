using System.Text.Json.Serialization;

namespace Nightware.Adapters.Directory.Core.Admin;

/// <summary>
/// The fields of Keycloak's <c>OrganizationRepresentation</c> the console reads
/// (https://www.keycloak.org/docs-api/latest/rest-api/index.html#OrganizationRepresentation). Updates
/// round-trip the raw JSON (<see cref="IKeycloakAdminClient.UpdateOrganizationAsync"/>), so unmodelled
/// fields and attributes the console does not own are preserved.
/// </summary>
public sealed record KeycloakOrganization
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>Stable handle; Keycloak refuses to change it after creation.</summary>
    [JsonPropertyName("alias")]
    public string? Alias { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    /// <summary>Included in brief representations too; a domain belongs to at most one organization per realm.</summary>
    [JsonPropertyName("domains")]
    public IReadOnlyList<KeycloakOrganizationDomain>? Domains { get; init; }

    /// <summary>Full representations only.</summary>
    [JsonPropertyName("attributes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? Attributes { get; init; }

    public string? Attribute(string name) =>
        Attributes is not null && Attributes.TryGetValue(name, out IReadOnlyList<string>? values) && values.Count > 0 ? values[0] : null;
}

/// <summary>Keycloak's <c>OrganizationDomainRepresentation</c>.</summary>
public sealed record KeycloakOrganizationDomain
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("verified")]
    public bool Verified { get; init; }
}

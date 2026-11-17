using System.Text.Json.Serialization;

namespace Nightware.Adapters.Directory.Core.Admin;

/// <summary>The fields of Keycloak's <c>GroupRepresentation</c> the console reads (organization groups included).</summary>
public sealed record KeycloakGroup
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("path")]
    public string? Path { get; init; }

    /// <summary>
    /// The parent group's id. For a "top-level" organization group this is the organization's internal backing
    /// group (Keycloak 26.8 <c>JpaOrganizationProvider.createGroup</c>), so it is not a top-level test.
    /// </summary>
    [JsonPropertyName("parentId")]
    public string? ParentId { get; init; }
}

/// <summary>
/// Keycloak's <c>RoleRepresentation</c> reduced to what role mappings need: Keycloak matches a mapping by
/// <see cref="Id"/> and refuses one whose id does not belong to the named role.
/// </summary>
public sealed record KeycloakRole
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }
}

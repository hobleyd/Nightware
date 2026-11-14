namespace Nightware.Adapters.Directory.Core.Admin;

/// <summary>
/// Query parameters of <c>GET /admin/realms/{realm}/users</c> (and <c>/users/count</c>). Every
/// property is optional; null means "not sent".
/// </summary>
public sealed record KeycloakUserQuery
{
    /// <summary>
    /// Matches username, first/last name or email. Keycloak treats <c>foo</c> as a prefix search,
    /// <c>*foo*</c> as infix and <c>"foo"</c> as exact. Not affected by <see cref="Exact"/>.
    /// </summary>
    public string? Search { get; init; }

    public string? Username { get; init; }

    public string? Email { get; init; }

    public string? FirstName { get; init; }

    public string? LastName { get; init; }

    /// <summary>When true, <see cref="Username"/>/<see cref="Email"/>/<see cref="FirstName"/>/<see cref="LastName"/> must match exactly.</summary>
    public bool? Exact { get; init; }

    public bool? Enabled { get; init; }

    /// <summary>Attribute filters, sent as <c>q=key1:value1 key2:value2</c> (all must match).</summary>
    public IReadOnlyDictionary<string, string>? Attributes { get; init; }

    /// <summary>Zero-based offset of the first result.</summary>
    public int? First { get; init; }

    /// <summary>Page size (Keycloak's default is 100).</summary>
    public int? Max { get; init; }

    /// <summary>When true, Keycloak omits attributes from each user.</summary>
    public bool? BriefRepresentation { get; init; }

    /// <summary>Exact, case-insensitive lookup by email.</summary>
    public static KeycloakUserQuery ByEmail(string email) => new() { Email = email, Exact = true };

    /// <summary>Exact lookup by username.</summary>
    public static KeycloakUserQuery ByUsername(string username) => new() { Username = username, Exact = true };

    internal string ToQueryString(bool includePaging)
    {
        List<string> parts = [];
        Add(parts, "search", Search);
        Add(parts, "username", Username);
        Add(parts, "email", Email);
        Add(parts, "firstName", FirstName);
        Add(parts, "lastName", LastName);
        Add(parts, "exact", Bool(Exact));
        Add(parts, "enabled", Bool(Enabled));
        if (Attributes is { Count: > 0 })
        {
            Add(parts, "q", string.Join(' ', Attributes.Select(static kv => $"{kv.Key}:{kv.Value}")));
        }

        if (includePaging)
        {
            Add(parts, "first", First?.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Add(parts, "max", Max?.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Add(parts, "briefRepresentation", Bool(BriefRepresentation));
        }

        return parts.Count == 0 ? string.Empty : "?" + string.Join('&', parts);
    }

    private static string? Bool(bool? value) => value switch
    {
        true => "true",
        false => "false",
        null => null,
    };

    private static void Add(List<string> parts, string name, string? value)
    {
        if (value is not null)
        {
            parts.Add($"{name}={Uri.EscapeDataString(value)}");
        }
    }
}

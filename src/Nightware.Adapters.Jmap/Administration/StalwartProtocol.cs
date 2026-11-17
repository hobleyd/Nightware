using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Core.Requests;

namespace Nightware.Adapters.Jmap.Administration;

// Stalwart v1 management objects (JMAP "x:" methods, capability urn:stalwart:jmap).
// Shapes: deploy/stalwart/configure.sh (known-working against v1.1.0) and the object reference at
// https://stalw.art/docs/ref/object/{account,domain,dkim-signature}/. Unlike RFC 8620 methods these
// calls carry no accountId (configure.sh never sends one), so the Core request types are not used.

/// <summary><c>x:Foo/get</c> arguments.</summary>
internal sealed record StalwartGetRequest
{
    public IReadOnlyList<string>? Ids { get; init; }

    public IReadOnlyList<string>? Properties { get; init; }
}

/// <summary><c>x:Foo/query</c> arguments.</summary>
internal sealed record StalwartQueryRequest<TFilter>
    where TFilter : class
{
    public TFilter? Filter { get; init; }

    public int? Position { get; init; }

    public int? Limit { get; init; }
}

/// <summary><c>x:Account/query</c> conditions documented for v1: <c>name</c>, <c>domainId</c> (also <c>text</c>, tenant and group ids). There is no address or alias filter.</summary>
internal sealed record AccountFilter
{
    public string? Name { get; init; }

    public string? DomainId { get; init; }
}

/// <summary><c>x:Domain/query</c> condition <c>name</c> (configure.sh uses it).</summary>
internal sealed record DomainFilter(string Name);

/// <summary><c>x:DkimSignature/query</c> condition <c>domainId</c> (documented example).</summary>
internal sealed record DkimSignatureFilter(string DomainId);

/// <summary><c>x:Foo/set</c> arguments. Objects are free-form because they are polymorphic (<c>@type</c>).</summary>
internal sealed record StalwartSetRequest
{
    public IReadOnlyDictionary<string, JsonObject>? Create { get; init; }

    public IReadOnlyDictionary<string, JsonObject>? Update { get; init; }

    public IReadOnlyList<string>? Destroy { get; init; }
}

/// <summary>What a <c>created</c> entry carries that the adapter needs.</summary>
internal sealed record StalwartCreated
{
    public string Id { get; init; } = string.Empty;
}

/// <summary>An <c>x:Account</c> as read. <c>@type</c> is <c>User</c> or <c>Group</c> (docs).</summary>
internal sealed record StalwartAccount
{
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("@type")]
    public string? Type { get; init; }

    public string? Name { get; init; }

    public string? DomainId { get; init; }

    /// <summary>Server-set primary address.</summary>
    public string? EmailAddress { get; init; }

    /// <summary>
    /// <c>EmailAlias</c> list (<c>{name, domainId, enable, description}</c>). Stalwart writes lists as maps
    /// keyed by index (<c>{"0": …}</c>); read either that or a JSON array.
    /// </summary>
    public JsonElement? Aliases { get; init; }

    /// <summary>Server-set bytes used (docs: <c>usedDiskQuota</c>, Size).</summary>
    public long? UsedDiskQuota { get; init; }

    /// <summary><c>UserRoles</c>: <c>{"@type":"User"}</c> (the default), <c>Admin</c> or <c>Custom</c> (docs).</summary>
    public JsonElement? Roles { get; init; }

    /// <summary><c>{"@type":"Inherit"}</c> (the default), <c>Merge</c> or <c>Replace</c> (docs).</summary>
    public JsonElement? Permissions { get; init; }

    /// <summary>
    /// Whether the account holds more than an ordinary user's rights: an <c>Admin</c> or <c>Custom</c> role (the
    /// console's own principal has one) or permissions of its own. Fails closed: Stalwart 1.1 always returns both
    /// fields (verified), so a missing or unreadable one counts as privileged rather than ordinary.
    /// </summary>
    public bool IsPrivileged =>
        !string.Equals(TypeOf(Roles), "User", StringComparison.Ordinal)
        || !string.Equals(TypeOf(Permissions), "Inherit", StringComparison.Ordinal);

    private static string? TypeOf(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Object } e && e.TryGetProperty("@type", out JsonElement type) && type.ValueKind == JsonValueKind.String
            ? type.GetString()
            : element is { ValueKind: JsonValueKind.Object } ? string.Empty : null;
}

/// <summary>An <c>x:Domain</c> as read.</summary>
internal sealed record StalwartDomain
{
    public string Id { get; init; } = string.Empty;

    public string? Name { get; init; }

    /// <summary>Server-set "current DNS zone data for the domain" (Text; format not documented).</summary>
    public string? DnsZoneFile { get; init; }
}

internal sealed record StalwartAlias(string Name, string DomainId);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    NumberHandling = JsonNumberHandling.Strict)]
[JsonSerializable(typeof(StalwartGetRequest))]
[JsonSerializable(typeof(StalwartQueryRequest<AccountFilter>))]
[JsonSerializable(typeof(StalwartQueryRequest<DomainFilter>))]
[JsonSerializable(typeof(StalwartQueryRequest<DkimSignatureFilter>))]
[JsonSerializable(typeof(StalwartSetRequest))]
[JsonSerializable(typeof(QueryResponse))]
[JsonSerializable(typeof(GetResponse<StalwartAccount>))]
[JsonSerializable(typeof(GetResponse<StalwartDomain>))]
[JsonSerializable(typeof(SetResponse<StalwartCreated>))]
internal sealed partial class StalwartJsonContext : JsonSerializerContext;

/// <summary>Capability and method descriptors of the management API.</summary>
internal static class StalwartMethods
{
    public const string Capability = "urn:stalwart:jmap";

    private static readonly string[] Caps = [Capability];

    private static StalwartJsonContext Json => StalwartJsonContext.Default;

    public static JmapMethod<StalwartQueryRequest<AccountFilter>, QueryResponse> AccountQuery { get; } =
        new("x:Account/query", JmapMethodKind.Query, Caps, Json.StalwartQueryRequestAccountFilter, Json.QueryResponse);

    public static JmapMethod<StalwartGetRequest, GetResponse<StalwartAccount>> AccountGet { get; } =
        new("x:Account/get", JmapMethodKind.Get, Caps, Json.StalwartGetRequest, Json.GetResponseStalwartAccount);

    public static JmapMethod<StalwartSetRequest, SetResponse<StalwartCreated>> AccountSet { get; } =
        new("x:Account/set", JmapMethodKind.Set, Caps, Json.StalwartSetRequest, Json.SetResponseStalwartCreated);

    public static JmapMethod<StalwartQueryRequest<DomainFilter>, QueryResponse> DomainQuery { get; } =
        new("x:Domain/query", JmapMethodKind.Query, Caps, Json.StalwartQueryRequestDomainFilter, Json.QueryResponse);

    public static JmapMethod<StalwartGetRequest, GetResponse<StalwartDomain>> DomainGet { get; } =
        new("x:Domain/get", JmapMethodKind.Get, Caps, Json.StalwartGetRequest, Json.GetResponseStalwartDomain);

    public static JmapMethod<StalwartSetRequest, SetResponse<StalwartCreated>> DomainSet { get; } =
        new("x:Domain/set", JmapMethodKind.Set, Caps, Json.StalwartSetRequest, Json.SetResponseStalwartCreated);

    public static JmapMethod<StalwartQueryRequest<DkimSignatureFilter>, QueryResponse> DkimSignatureQuery { get; } =
        new("x:DkimSignature/query", JmapMethodKind.Query, Caps, Json.StalwartQueryRequestDkimSignatureFilter, Json.QueryResponse);

    public static JmapMethod<StalwartSetRequest, SetResponse<StalwartCreated>> ActionSet { get; } =
        new("x:Action/set", JmapMethodKind.Set, Caps, Json.StalwartSetRequest, Json.SetResponseStalwartCreated);

    /// <summary>Reads <see cref="StalwartAccount.Aliases"/> whether Stalwart sends an index-keyed map or an array.</summary>
    public static IReadOnlyList<StalwartAlias> ReadAliases(JsonElement? aliases)
    {
        if (aliases is not { } element)
        {
            return [];
        }

        IEnumerable<JsonElement> items = element.ValueKind switch
        {
            JsonValueKind.Array => element.EnumerateArray(),
            JsonValueKind.Object => element.EnumerateObject().Select(static p => p.Value),
            _ => [],
        };

        var result = new List<StalwartAlias>();
        foreach (JsonElement item in items)
        {
            if (item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("name", out JsonElement name) && name.ValueKind == JsonValueKind.String
                && item.TryGetProperty("domainId", out JsonElement domainId) && domainId.ValueKind == JsonValueKind.String)
            {
                result.Add(new StalwartAlias(name.GetString()!, domainId.GetString()!));
            }
        }

        return result;
    }
}
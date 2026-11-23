using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nightware.Adapters.Dav.Core;

/// <summary>
/// One OCS request (ADR 0009 D8): a path below the OCS root, query, an optional form or JSON body, and extra
/// headers. Talk takes JSON for nested parameters (<c>participants</c>) and needs its session cookie for calls,
/// which the cookie-free client never keeps on its own.
/// </summary>
public sealed record OcsRequest(HttpMethod Method, string Path)
{
    public IReadOnlyDictionary<string, string>? Query { get; init; }

    /// <summary>A form body; ignored when <see cref="Json"/> is set.</summary>
    public IReadOnlyDictionary<string, string>? Form { get; init; }

    public JsonNode? Json { get; init; }

    /// <summary>Extra request headers (e.g. <c>Cookie</c>). <c>Authorization</c> and <c>OCS-APIRequest</c> are the client's own.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    public static OcsRequest Get(string path, IReadOnlyDictionary<string, string>? query = null) =>
        new(HttpMethod.Get, path) { Query = query };
}

/// <summary>A successful OCS answer: <c>ocs.data</c>, the HTTP status and the response headers.</summary>
public sealed record OcsResponse(JsonElement Data, int Status, IReadOnlyDictionary<string, IReadOnlyList<string>> Headers)
{
    /// <summary>The first value of a header (case-insensitive), or null.</summary>
    public string? Header(string name) =>
        Headers.TryGetValue(name, out var values) && values.Count > 0 ? values[0] : null;

    /// <summary>Every <c>Set-Cookie</c> value.</summary>
    public IReadOnlyList<string> SetCookies =>
        Headers.TryGetValue("Set-Cookie", out var values) ? values : [];
}

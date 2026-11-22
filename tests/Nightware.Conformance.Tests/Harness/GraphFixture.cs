using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nightware.Conformance.Tests.Harness;

/// <summary>
/// One golden request/response pair (docs/contract/endpoints.md "Fixture format"):
/// <c>{"operationId","provenance","request":{method,path,query,headers,body|bodyBase64},"response":{status,headers,body|bodyBase64}}</c>.
/// </summary>
public sealed record GraphFixture(
    string OperationId,
    string? Provenance,
    FixtureRequest Request,
    FixtureResponse Response,
    string SourceFile)
{
    public static GraphFixture Load(string path)
    {
        var root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })?.AsObject()
            ?? throw new InvalidDataException($"{path} is not a JSON object.");
        var request = root["request"]?.AsObject() ?? throw new InvalidDataException($"{path} has no request.");
        var response = root["response"]?.AsObject() ?? throw new InvalidDataException($"{path} has no response.");

        return new GraphFixture(
            root["operationId"]?.GetValue<string>() ?? throw new InvalidDataException($"{path} has no operationId."),
            root["provenance"]?.GetValue<string>(),
            new FixtureRequest(
                request["method"]?.GetValue<string>() ?? "GET",
                request["path"]?.GetValue<string>() ?? throw new InvalidDataException($"{path} has no request.path."),
                Strings(request["query"]),
                Strings(request["headers"]),
                request["body"]?.DeepClone())
            {
                BodyBase64 = request["bodyBase64"]?.GetValue<string>(),
            },
            new FixtureResponse(
                response["status"]?.GetValue<int>() ?? throw new InvalidDataException($"{path} has no response.status."),
                Strings(response["headers"]),
                response["body"]?.DeepClone(),
                response["bodyBase64"]?.GetValue<string>()),
            path);
    }

    private static Dictionary<string, string> Strings(JsonNode? node) =>
        node is JsonObject obj
            ? obj.Where(static p => p.Value is not null).ToDictionary(static p => p.Key, static p => p.Value!.ToString(), StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}

/// <param name="Path">Includes the version segment; already URL-path encoded (e.g. <c>users/a%40b</c>).</param>
/// <param name="Query">Decoded option values, e.g. <c>{"$select": "id,displayName"}</c>.</param>
public sealed record FixtureRequest(string Method, string Path, IReadOnlyDictionary<string, string> Query, IReadOnlyDictionary<string, string> Headers, JsonNode? Body)
{
    /// <summary>A binary request body (an image upload), sent as these bytes; <see cref="Body"/> is then null.</summary>
    public string? BodyBase64 { get; init; }

    public string Url =>
        Query.Count == 0
            ? Path
            : Path + "?" + string.Join('&', Query.Select(static q => $"{EscapeKey(q.Key)}={Uri.EscapeDataString(q.Value)}"));

    private static string EscapeKey(string key) =>
        key.StartsWith('$') ? "$" + Uri.EscapeDataString(key[1..]) : Uri.EscapeDataString(key);
}

/// <param name="Body">A JSON body, a string for raw text payloads, or null for empty bodies.</param>
/// <param name="BodyBase64">Binary payloads.</param>
public sealed record FixtureResponse(int Status, IReadOnlyDictionary<string, string> Headers, JsonNode? Body, string? BodyBase64);

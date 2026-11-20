using System.Globalization;
using System.Text.Json.Nodes;

using Nightware.Domain.Kernel;

namespace Nightware.Api.Graph.Batch;

/// <summary>One entry of a <c>$batch</c> body's <c>requests</c> array.</summary>
public sealed record BatchItem(
    string Id,
    string Method,
    string Url,
    IReadOnlyList<string> DependsOn,
    IReadOnlyDictionary<string, string> Headers,
    JsonNode? Body);

/// <summary>
/// Validates a <c>$batch</c> body as a whole (ADR 0006 D9). What is wrong with the batch itself
/// (shape, ids, <c>dependsOn</c>, size) fails the batch; what is wrong with one request's URL or body
/// is answered by that request alone, in <see cref="BatchDispatcher"/>.
/// </summary>
public static class BatchRequestReader
{
    /// <summary>Graph's limit on requests in one batch.</summary>
    public const int MaxRequests = 20;

    public static Result<IReadOnlyList<BatchItem>> Read(JsonObject? body)
    {
        if (body is null || Property(body, "requests") is not JsonArray requests)
        {
            return Error.Validation("A batch body must have a 'requests' array.");
        }

        if (requests.Count == 0)
        {
            return Error.Validation("A batch must contain at least one request.");
        }

        if (requests.Count > MaxRequests)
        {
            return Error.Validation(string.Create(CultureInfo.InvariantCulture,
                $"Number of included requests in the request is too large: {requests.Count}. The maximum is {MaxRequests}."));
        }

        var items = new List<BatchItem>(requests.Count);
        foreach (var node in requests)
        {
            var item = ReadItem(node);
            if (!item.IsSuccess)
            {
                return item.Error;
            }

            if (items.Any(i => string.Equals(i.Id, item.Value.Id, StringComparison.OrdinalIgnoreCase)))
            {
                return Error.Validation($"Request id '{item.Value.Id}' appears more than once.");
            }

            items.Add(item.Value);
        }

        var ids = items.Select(static i => i.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (item.DependsOn.FirstOrDefault(d => !ids.Contains(d)) is { } unknown)
            {
                return Error.Validation($"Request '{item.Id}' depends on '{unknown}', which is not in the batch.");
            }
        }

        return HasCycle(items)
            ? Error.Validation("The batch's dependsOn references form a cycle.")
            : Result<IReadOnlyList<BatchItem>>.Success(items);
    }

    private static Result<BatchItem> ReadItem(JsonNode? node)
    {
        if (node is not JsonObject request)
        {
            return Error.Validation("Each batch request must be a JSON object.");
        }

        var id = Text(request, "id");
        var method = Text(request, "method");
        var url = Text(request, "url");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(method) || url is null)
        {
            return Error.Validation("Each batch request needs an 'id', a 'method' and a 'url'.");
        }

        var dependsOn = new List<string>();
        switch (Property(request, "dependsOn"))
        {
            case null:
                break;
            case JsonArray array when array.All(static d => d is JsonValue v && v.TryGetValue<string>(out _)):
                dependsOn.AddRange(array.Select(static d => d!.GetValue<string>()));
                break;
            default:
                return Error.Validation($"Request '{id}': 'dependsOn' must be an array of request ids.");
        }

        if (dependsOn.Contains(id, StringComparer.OrdinalIgnoreCase))
        {
            return Error.Validation($"Request '{id}' depends on itself.");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        switch (Property(request, "headers"))
        {
            case null:
                break;
            case JsonObject map:
                foreach (var (name, value) in map)
                {
                    if (value is not JsonValue v || !v.TryGetValue<string>(out var text))
                    {
                        return Error.Validation($"Request '{id}': header '{name}' must be a string.");
                    }

                    headers[name] = text;
                }

                break;
            default:
                return Error.Validation($"Request '{id}': 'headers' must be an object.");
        }

        return new BatchItem(id, method.ToUpperInvariant(), url, dependsOn, headers, Property(request, "body")?.DeepClone());
    }

    private static bool HasCycle(IReadOnlyList<BatchItem> items)
    {
        var byId = items.ToDictionary(static i => i.Id, StringComparer.OrdinalIgnoreCase);
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var onPath = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool Visit(string id)
        {
            if (done.Contains(id))
            {
                return false;
            }

            if (!onPath.Add(id))
            {
                return true;
            }

            var cyclic = byId[id].DependsOn.Any(Visit);
            onPath.Remove(id);
            done.Add(id);
            return cyclic;
        }

        return items.Any(i => Visit(i.Id));
    }

    // Graph matches batch member names case-insensitively, as it does request bodies.
    private static JsonNode? Property(JsonObject obj, string name) =>
        obj.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static string? Text(JsonObject obj, string name) =>
        Property(obj, name) is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

using System.Text.Json.Nodes;

namespace Nightware.Conformance.Tests.Harness;

/// <summary>
/// Removes what a fixture cannot pin down (docs/contract/endpoints.md "Fixture format"): opaque
/// <c>@odata.context</c>/<c>@odata.etag</c>, continuation tokens, and the per-request <c>innerError</c>
/// ids/date. Continuation links must still start with the configured Graph base; past that check they
/// compare as <see cref="LinkPlaceholder"/>.
/// </summary>
public static class ResponseNormaliser
{
    public const string LinkPlaceholder = "<link>";
    public const string VolatilePlaceholder = "<volatile>";

    private static readonly HashSet<string> Dropped = new(StringComparer.Ordinal) { "@odata.context", "@odata.etag" };
    private static readonly HashSet<string> Links = new(StringComparer.Ordinal) { "@odata.nextLink", "@odata.deltaLink" };
    private static readonly HashSet<string> InnerErrorVolatile = new(StringComparer.Ordinal) { "date", "request-id", "client-request-id" };

    /// <summary>Normalises <paramref name="node"/> in place; returns the links it found for host checks.</summary>
    public static IReadOnlyList<string> Normalise(JsonNode? node, IEnumerable<string>? ignorePaths = null)
    {
        var links = new List<string>();
        Walk(node, links, inInnerError: false);
        foreach (var path in ignorePaths ?? [])
        {
            Remove(node, path.Split('/', StringSplitOptions.RemoveEmptyEntries), 0);
        }

        return links;
    }

    private static void Walk(JsonNode? node, List<string> links, bool inInnerError)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(static p => p.Key).ToList())
                {
                    if (Dropped.Contains(key))
                    {
                        obj.Remove(key);
                    }
                    else if (Links.Contains(key))
                    {
                        links.Add(obj[key]?.ToString() ?? string.Empty);
                        obj[key] = LinkPlaceholder;
                    }
                    else if (inInnerError && InnerErrorVolatile.Contains(key))
                    {
                        obj[key] = VolatilePlaceholder;
                    }
                    else
                    {
                        Walk(obj[key], links, key == "innerError");
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    Walk(item, links, inInnerError: false);
                }

                break;
        }
    }

    private static void Remove(JsonNode? node, string[] segments, int index)
    {
        if (node is null || index >= segments.Length)
        {
            return;
        }

        var segment = segments[index];
        var last = index == segments.Length - 1;
        switch (node)
        {
            case JsonObject obj when segment == "*":
                foreach (var key in obj.Select(static p => p.Key).ToList())
                {
                    if (last)
                    {
                        obj.Remove(key);
                    }
                    else
                    {
                        Remove(obj[key], segments, index + 1);
                    }
                }

                break;
            case JsonObject obj:
                if (last)
                {
                    obj.Remove(segment);
                }
                else
                {
                    Remove(obj[segment], segments, index + 1);
                }

                break;
            case JsonArray array when segment == "*":
                foreach (var item in array.ToList())
                {
                    if (last)
                    {
                        array.Remove(item);
                    }
                    else
                    {
                        Remove(item, segments, index + 1);
                    }
                }

                break;
        }
    }
}

using System.Text.Json.Nodes;

namespace Nightware.Conformance.Tests.Harness;

/// <summary>
/// Comparisons for Graph-parity fixtures (ADR 0006 D2), where Exchange answers with members the façade does not
/// have (change keys, Teams settings, Outlook links) and the façade may add members Graph left out.
/// </summary>
public static class ParityNormalisers
{
    /// <summary>
    /// Keeps, at every level, only the object members both bodies have: every value the façade emits that Graph
    /// also emits must match, and neither side's extras fail the comparison.
    /// </summary>
    public static void SharedMembers(JsonNode expected, JsonNode actual)
    {
        switch (expected, actual)
        {
            case (JsonObject e, JsonObject a):
                foreach (var key in e.Select(static p => p.Key).Where(k => !a.ContainsKey(k)).ToList())
                {
                    e.Remove(key);
                }

                foreach (var key in a.Select(static p => p.Key).Where(k => !e.ContainsKey(k)).ToList())
                {
                    a.Remove(key);
                }

                foreach (var (key, value) in e.ToList())
                {
                    if (value is not null && a[key] is { } other)
                    {
                        SharedMembers(value, other);
                    }
                }

                break;
            case (JsonArray e, JsonArray a):
                for (var i = 0; i < Math.Min(e.Count, a.Count); i++)
                {
                    if (e[i] is { } x && a[i] is { } y)
                    {
                        SharedMembers(x, y);
                    }
                }

                break;
        }
    }

    /// <summary>Reduces each item of <c>value</c> (or the body itself) to <paramref name="members"/>.</summary>
    public static Action<JsonNode, JsonNode> Only(params string[] members) => (expected, actual) =>
    {
        foreach (var body in new[] { expected, actual })
        {
            foreach (var item in body["value"] is JsonArray list ? list.OfType<JsonObject>() : [body.AsObject()])
            {
                foreach (var key in item.Select(static p => p.Key).Where(k => !members.Contains(k)).ToList())
                {
                    item.Remove(key);
                }
            }
        }
    };
}

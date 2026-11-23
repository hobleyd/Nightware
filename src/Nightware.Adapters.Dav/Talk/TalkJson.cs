using System.Globalization;
using System.Text.Json;

namespace Nightware.Adapters.Dav.Talk;

/// <summary>Reads Talk's OCS data leniently: a missing or differently typed member is null, never an exception.</summary>
internal static class TalkJson
{
    public static string? Str(JsonElement element, string name) =>
        Member(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    public static long? Long(JsonElement element, string name) =>
        Member(element, name) switch
        {
            { ValueKind: JsonValueKind.Number } number when number.TryGetInt64(out var n) => n,
            { ValueKind: JsonValueKind.String } text when long.TryParse(text.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
            _ => null,
        };

    public static int? Int(JsonElement element, string name) => Long(element, name) is { } n and >= int.MinValue and <= int.MaxValue ? (int)n : null;

    public static bool Bool(JsonElement element, string name) => Member(element, name) is { ValueKind: JsonValueKind.True };

    /// <summary>The elements of an array (an empty sequence for anything else).</summary>
    public static IEnumerable<JsonElement> Items(JsonElement element) =>
        element.ValueKind == JsonValueKind.Array ? element.EnumerateArray() : [];

    private static JsonElement? Member(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : null;
}

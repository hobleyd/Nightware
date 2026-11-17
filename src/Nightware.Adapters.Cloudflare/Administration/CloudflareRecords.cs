using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

using Nightware.Domain.Administration;

namespace Nightware.Adapters.Cloudflare.Administration;

/// <summary>
/// Cloudflare's DNS record JSON to and from <see cref="DnsRecord"/>'s value format. Cloudflare keeps an MX or SRV
/// priority beside <c>content</c>, and SRV, CAA and TLSA in a <c>data</c> object; a TXT value is written as quoted
/// strings of at most 255 characters (one DNS character-string each), which Cloudflare would otherwise split itself.
/// </summary>
internal static class CloudflareRecords
{
    /// <summary>Cloudflare's "automatic" TTL.</summary>
    internal const int AutomaticTtl = 1;

    private const int TxtStringLength = 255;

    /// <summary>Recorded on every record the console creates, so it can be told apart in Cloudflare's dashboard.</summary>
    internal const string Comment = "Published by the Nightware console";

    public static ZoneRecord? ToZoneRecord(JsonObject json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Text(json["id"]) is not { } id || Text(json["name"]) is not { } name || Text(json["type"]) is not { } type)
        {
            return null;
        }

        string content = Text(json["content"]) ?? string.Empty;
        JsonObject? data = json["data"] as JsonObject;
        string value = type.ToUpperInvariant() switch
        {
            "MX" => $"{Number(json["priority"])} {content}",
            "SRV" when data is not null => $"{Number(data["priority"])} {Number(data["weight"])} {Number(data["port"])} {Text(data["target"])}",
            "SRV" => $"{Number(json["priority"])} {content}",
            "CAA" when data is not null => $"{Number(data["flags"])} {Text(data["tag"])} \"{Text(data["value"])}\"",
            "TLSA" when data is not null => $"{Number(data["usage"])} {Number(data["selector"])} {Number(data["matching_type"])} {Text(data["certificate"])}",
            _ => content,
        };
        int? ttl = json["ttl"] is JsonValue t && t.TryGetValue(out int seconds) && seconds != AutomaticTtl ? seconds : null;
        bool proxied = json["proxied"] is JsonValue p && p.TryGetValue(out bool on) && on;
        return new ZoneRecord(id, new DnsRecord(name, type, ttl, value), proxied);
    }

    /// <summary>The body that creates or overwrites a record with <paramref name="record"/> (canonical, <see cref="DnsPublicationPlanner.Canonical"/>).</summary>
    public static JsonObject ToBody(DnsRecord record, bool create)
    {
        ArgumentNullException.ThrowIfNull(record);
        string type = record.Type.ToUpperInvariant();
        string[] parts = record.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var body = new JsonObject
        {
            ["type"] = type,
            ["name"] = record.Name,
            ["ttl"] = record.Ttl is { } ttl ? Math.Clamp(ttl, 60, 86400) : AutomaticTtl,
        };
        if (create)
        {
            body["comment"] = Comment;
        }

        switch (type)
        {
            case "MX" when parts.Length == 2 && Numeric(parts, 1):
                body["priority"] = Int(parts[0]);
                body["content"] = parts[1];
                break;
            case "SRV" when parts.Length == 4 && Numeric(parts, 3):
                body["data"] = new JsonObject { ["priority"] = Int(parts[0]), ["weight"] = Int(parts[1]), ["port"] = Int(parts[2]), ["target"] = parts[3] };
                break;
            case "CAA" when parts.Length >= 3 && Numeric(parts, 1):
                body["data"] = new JsonObject { ["flags"] = Int(parts[0]), ["tag"] = parts[1], ["value"] = string.Join(' ', parts.Skip(2)).Trim('"') };
                break;
            case "TLSA" when parts.Length == 4 && Numeric(parts, 3):
                body["data"] = new JsonObject { ["usage"] = Int(parts[0]), ["selector"] = Int(parts[1]), ["matching_type"] = Int(parts[2]), ["certificate"] = parts[3] };
                break;
            case "TXT":
                body["content"] = QuotedStrings(record.Value);
                break;
            default:
                body["content"] = record.Value;
                break;
        }

        if (type is "A" or "AAAA" or "CNAME")
        {
            // Mail clients and servers need the real address, never Cloudflare's proxy.
            body["proxied"] = false;
        }

        return body;
    }

    /// <summary><c>"…" "…"</c>: the value in character-strings of at most 255 characters, quotes and backslashes escaped.</summary>
    internal static string QuotedStrings(string value) =>
        value.Length == 0
            ? "\"\""
            : string.Join(' ', value.Chunk(TxtStringLength).Select(static chunk =>
                "\"" + new string(chunk).Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\""));

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static string Number(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue(out int n) ? n.ToString(CultureInfo.InvariantCulture) : Text(node) ?? "0";

    private static int Int(string text) => int.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);

    /// <summary>Whether the first <paramref name="count"/> fields are whole numbers (otherwise the value is sent as plain content).</summary>
    private static bool Numeric(string[] parts, int count) =>
        parts.Take(count).All(static p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out _));
}

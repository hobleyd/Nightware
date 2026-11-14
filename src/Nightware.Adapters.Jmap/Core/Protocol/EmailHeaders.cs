using System.Text.Json;

namespace Nightware.Adapters.Jmap.Core.Protocol;

/// <summary>Parsed forms of a header property (RFC 8621 §4.1.2).</summary>
internal enum HeaderForm
{
    Raw,
    Text,
    Addresses,
    GroupedAddresses,
    MessageIds,
    Date,
    Urls,
}

/// <summary>
/// Builds and reads dynamic <c>header:{name}[:as{Form}][:all]</c> Email properties,
/// e.g. <c>header:Message-ID:asMessageIds</c> or <c>header:List-Unsubscribe:asURLs:all</c>.
/// </summary>
internal static class EmailHeaders
{
    /// <summary>The property name to put in <c>Email/get</c> <c>properties</c>.</summary>
    public static string Property(string headerName, HeaderForm form = HeaderForm.Raw, bool all = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(headerName);
        if (headerName.Contains(':', StringComparison.Ordinal))
        {
            throw new ArgumentException("Header names cannot contain ':'.", nameof(headerName));
        }

        var suffix = form switch
        {
            HeaderForm.Raw => string.Empty,
            HeaderForm.Text => ":asText",
            HeaderForm.Addresses => ":asAddresses",
            HeaderForm.GroupedAddresses => ":asGroupedAddresses",
            HeaderForm.MessageIds => ":asMessageIds",
            HeaderForm.Date => ":asDate",
            HeaderForm.Urls => ":asURLs",
            _ => throw new ArgumentOutOfRangeException(nameof(form)),
        };
        return $"header:{headerName}{suffix}{(all ? ":all" : string.Empty)}";
    }

    /// <summary>
    /// Reads a header property from an Email's extension data. Header names are
    /// case-insensitive, so the lookup is too. Returns false when the property was
    /// not fetched; a fetched-but-absent header yields a JSON <c>null</c> element.
    /// </summary>
    public static bool TryGet(Email email, string headerName, HeaderForm form, bool all, out JsonElement value)
    {
        ArgumentNullException.ThrowIfNull(email);
        return TryGet(email.Extra, Property(headerName, form, all), out value);
    }

    /// <summary>A text-form header (<c>:asText</c>), or null when absent/not fetched.</summary>
    public static string? GetText(Email email, string headerName)
    {
        return TryGet(email, headerName, HeaderForm.Text, all: false, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    /// <summary>A <c>:asMessageIds</c> header, or empty when absent/not fetched.</summary>
    public static IReadOnlyList<string> GetMessageIds(Email email, string headerName)
    {
        if (!TryGet(email, headerName, HeaderForm.MessageIds, all: false, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)];
    }

    private static bool TryGet(Dictionary<string, JsonElement>? extra, string property, out JsonElement value)
    {
        value = default;
        if (extra is null)
        {
            return false;
        }

        if (extra.TryGetValue(property, out value))
        {
            return true;
        }

        foreach (var (key, element) in extra)
        {
            if (string.Equals(key, property, StringComparison.OrdinalIgnoreCase))
            {
                value = element;
                return true;
            }
        }

        return false;
    }
}

using System.Globalization;

namespace Nightware.Adapters.Directory.Administration;

/// <summary>
/// Sealed text as the values of one Keycloak attribute, which keeps each value in 255 characters. Text that fits is
/// one value as it is (as every credential was stored before longer ones existed); longer text is split into values
/// <c>00:…</c>, <c>01:…</c> that are joined in that order. Sealed text is base64url, so it never holds a colon: a
/// value with one is a chunk.
/// </summary>
internal static class SealedChunks
{
    /// <summary>Longest sealed text stored: 99 chunks.</summary>
    public const int MaxLength = 99 * ChunkLength;

    private const int ChunkLength = KeycloakEntityRegistry.MaxAttributeLength - 3;

    public static IReadOnlyList<string> Split(string sealedText)
    {
        ArgumentNullException.ThrowIfNull(sealedText);
        if (sealedText.Length <= KeycloakEntityRegistry.MaxAttributeLength)
        {
            return [sealedText];
        }

        return [.. sealedText.Chunk(ChunkLength).Select(static (chunk, i) => $"{i.ToString("D2", CultureInfo.InvariantCulture)}:{new string(chunk)}")];
    }

    /// <summary>The sealed text, or null when there is none or the chunks are not a whole set (which no seal opens).</summary>
    public static string? Join(IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 1 && !values[0].Contains(':', StringComparison.Ordinal))
        {
            return values[0];
        }

        var chunks = new SortedDictionary<int, string>();
        foreach (string value in values)
        {
            int colon = value.IndexOf(':', StringComparison.Ordinal);
            if (colon < 1 || !int.TryParse(value.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out int index) || !chunks.TryAdd(index, value[(colon + 1)..]))
            {
                return null;
            }
        }

        return chunks.Count > 0 && chunks.Keys.Last() == chunks.Count - 1 ? string.Concat(chunks.Values) : null;
    }
}

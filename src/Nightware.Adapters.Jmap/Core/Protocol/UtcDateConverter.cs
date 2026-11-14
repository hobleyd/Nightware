using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nightware.Adapters.Jmap.Core.Protocol;

/// <summary>
/// Writes <see cref="DateTimeOffset"/> as a JMAP <c>UTCDate</c> (RFC 8620 §1.4):
/// UTC, <c>Z</c> suffix, fractional seconds only when non-zero. Reads any RFC 3339 date-time.
/// </summary>
internal sealed class UtcDateConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        DateTimeOffset.Parse(reader.GetString() ?? throw new JsonException("Expected a date string."), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(Format(value));
    }

    public static string Format(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return utc.Ticks % TimeSpan.TicksPerSecond == 0
            ? utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
            : utc.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);
    }
}

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nightware.Api.Features.Tasks.Dto;

/// <summary>
/// An instant as Graph's To Do service writes it: UTC with seven fractional digits,
/// <c>2026-06-01T10:05:00.1234567Z</c>. Used only on To Do DTOs.
/// </summary>
public sealed class PreciseInstantConverter : JsonConverter<DateTimeOffset?>
{
    public const string Format = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    public override bool HandleNull => false;

    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)
            ? value
            : throw new JsonException($"'{text}' is not a valid date-time.");
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value!.Value.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture));
    }
}

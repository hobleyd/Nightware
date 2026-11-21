using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nightware.Api.Features.Tasks.Dto;

/// <summary>
/// An instant as Graph's To Do service writes it: UTC with up to seven fractional digits and no trailing
/// zeros, <c>2026-11-20T22:39:06.231183Z</c> (and <c>…:09Z</c> on a whole second). Used only on To Do DTOs.
/// </summary>
public class PreciseInstantConverter : JsonConverter<DateTimeOffset?>
{
    public const string Format = "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'";

    /// <summary>The format written; <see cref="PaddedInstantConverter"/> keeps all seven digits.</summary>
    protected virtual string WriteFormat => Format;

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
        writer.WriteStringValue(value!.Value.UtcDateTime.ToString(WriteFormat, CultureInfo.InvariantCulture));
    }
}

/// <summary>Seven fractional digits always (<c>…:00.0000000Z</c>), as NightMail's upload-session parser was written against.</summary>
public sealed class PaddedInstantConverter : PreciseInstantConverter
{
    public const string PaddedFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    protected override string WriteFormat => PaddedFormat;
}

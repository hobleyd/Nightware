using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Nightware.Api.Graph.Json;

/// <summary>
/// System.Text.Json settings that reproduce Graph's wire format.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>camelCase properties; enums as camelCase strings (<c>notFlagged</c>, <c>meetingRequest</c>).</item>
/// <item>Instants (<see cref="DateTimeOffset"/>) as UTC with a <c>Z</c> and whole seconds:
/// <c>2026-01-01T10:00:00Z</c>.</item>
/// <item>Wall-clock times (<see cref="DateTime"/>, used only inside <c>dateTimeTimeZone</c>) with
/// seven fractional digits and <b>no</b> <c>Z</c>: <c>2026-01-01T10:00:00.0000000</c>. The client
/// appends the <c>Z</c> itself; emitting one breaks its <c>getSchedule</c> parser.</item>
/// <item><see cref="TimeOnly"/> as <c>08:00:00.0000000</c> (working hours).</item>
/// <item>Nulls are written (Graph does); DTO members that Graph omits when absent carry
/// <c>[JsonIgnore(Condition = WhenWritingNull)]</c>.</item>
/// <item>Relaxed escaping: HTML bodies and non-ASCII text are written as-is, like Graph.</item>
/// </list>
/// </remarks>
public static class GraphJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    /// <summary>Serialises a DTO (using its runtime type, so derived DTOs keep their members) to a mutable node.</summary>
    public static JsonObject ToObject(object dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        return JsonSerializer.SerializeToNode(dto, dto.GetType(), Options) as JsonObject
            ?? throw new InvalidOperationException($"{dto.GetType().Name} does not serialise to a JSON object.");
    }

    /// <summary>Applies the Graph settings to an existing options instance (used for the ASP.NET Core defaults).</summary>
    public static void Configure(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.DictionaryKeyPolicy = null;
        options.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        options.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        options.PropertyNameCaseInsensitive = true;
        options.NumberHandling = JsonNumberHandling.Strict;
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        options.Converters.Add(new GraphInstantConverter());
        options.Converters.Add(new GraphWallClockConverter());
        options.Converters.Add(new GraphTimeOfDayConverter());
    }

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        Configure(options);
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

/// <summary>Formats Graph instants and wall-clock times. Shared by the converters and link/OData code.</summary>
public static class GraphDateTimeFormat
{
    public const string InstantFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";
    public const string WallClockFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff";
    public const string TimeOfDayFormat = "HH:mm:ss.fffffff";

    public static string Instant(DateTimeOffset value) =>
        value.UtcDateTime.ToString(InstantFormat, CultureInfo.InvariantCulture);

    public static string WallClock(DateTime value) =>
        value.ToString(WallClockFormat, CultureInfo.InvariantCulture);
}

internal sealed class GraphInstantConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)
            ? value
            : throw new JsonException($"'{text}' is not a valid date-time.");
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(GraphDateTimeFormat.Instant(value));
}

internal sealed class GraphWallClockConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value))
        {
            throw new JsonException($"'{text}' is not a valid date-time.");
        }

        // A trailing Z/offset is tolerated on input but the value is wall-clock: the zone travels separately.
        return value.Kind == DateTimeKind.Local ? DateTime.SpecifyKind(value.ToUniversalTime(), DateTimeKind.Unspecified) : DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
    }

    public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
        writer.WriteStringValue(GraphDateTimeFormat.WallClock(value));
}

internal sealed class GraphTimeOfDayConverter : JsonConverter<TimeOnly>
{
    public override TimeOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        return TimeOnly.TryParse(text, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new JsonException($"'{text}' is not a valid time of day.");
    }

    public override void Write(Utf8JsonWriter writer, TimeOnly value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString(GraphDateTimeFormat.TimeOfDayFormat, CultureInfo.InvariantCulture));
}

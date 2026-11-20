using System.Globalization;
using System.Text.Json.Nodes;
using Nightware.Domain.Calendar;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Mail;

/// <summary>
/// The Graph-only state of a message, kept in its shared JMAP <c>metadata</c> under <see cref="Namespace"/>
/// (ADR 0006 D5): what Exchange stores on the item and an RFC 5322 message cannot hold without changing
/// its id. <c>flagged</c> itself stays the <c>$flagged</c> keyword that every IMAP client sees.
/// </summary>
/// <remarks>
/// <code>{"flag": {"status": "flagged|complete", "start"|"due"|"completed": {"dateTime", "timeZone"}},
///  "categories": ["…"], "importance": "low|normal|high"}</code>
/// Stalwart patches inside a namespace only once it exists, so writes replace the whole object
/// (<see cref="Apply"/>), and caps it at 8192 bytes.
/// </remarks>
internal sealed record MessageMetadata
{
    public const string Namespace = "nightware.ms";

    /// <summary>Stalwart's limit on one namespace's value.</summary>
    public const int MaxBytes = 8192;

    private const string DateFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff";

    public FollowUpFlag? Flag { get; init; }

    public IReadOnlyList<string>? Categories { get; init; }

    public Importance? Importance { get; init; }

    public static MessageMetadata Empty { get; } = new();

    /// <summary>Reads the namespace out of an Email's <c>metadata</c>; anything malformed reads as unset.</summary>
    public static MessageMetadata Read(JsonObject? metadata)
    {
        if (metadata?[Namespace] is not JsonObject ns)
        {
            return Empty;
        }

        return new MessageMetadata
        {
            Flag = ns["flag"] is JsonObject flag ? ReadFlag(flag) : null,
            Categories = ns["categories"] is JsonArray categories
                ? [.. categories.OfType<JsonValue>().Select(static c => c.TryGetValue<string>(out var name) ? name : null).OfType<string>()]
                : null,
            Importance = Text(ns, "importance") is { } importance && Enum.TryParse<Importance>(importance, ignoreCase: true, out var value) ? value : null,
        };
    }

    /// <summary>
    /// This state with <paramref name="patch"/>'s flag, categories and importance applied: a flag the
    /// <c>$flagged</c> keyword says all of, an empty category list and normal importance clear their entry.
    /// </summary>
    public MessageMetadata Apply(MessagePatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return this with
        {
            Flag = patch.Flag is { } flag ? (CarriesMore(flag) ? flag : null) : Flag,
            Categories = patch.Categories is { } categories ? (categories.Count == 0 ? null : categories) : Categories,
            Importance = patch.Importance is { } importance ? (importance == Domain.Mail.Importance.Normal ? null : importance) : Importance,
        };
    }

    /// <summary>A flag says more than <c>$flagged</c> can: it is complete, or it has dates.</summary>
    private static bool CarriesMore(FollowUpFlag flag) =>
        flag.Status == FlagStatus.Complete
        || flag.Status == FlagStatus.Flagged && (flag.StartDateTime is not null || flag.DueDateTime is not null || flag.CompletedDateTime is not null);

    /// <summary>The namespace's value, or null when nothing is set (which removes it).</summary>
    public JsonObject? ToJson()
    {
        var ns = new JsonObject();
        if (Flag is { } flag)
        {
            var json = new JsonObject { ["status"] = flag.Status == FlagStatus.Complete ? "complete" : "flagged" };
            AddDate(json, "start", flag.StartDateTime);
            AddDate(json, "due", flag.DueDateTime);
            AddDate(json, "completed", flag.CompletedDateTime);
            ns["flag"] = json;
        }

        if (Categories is { Count: > 0 } categories)
        {
            ns["categories"] = new JsonArray([.. categories.Select(static c => (JsonNode?)c)]);
        }

        if (Importance is { } importance)
        {
            ns["importance"] = importance.ToString().ToLowerInvariant();
        }

        return ns.Count == 0 ? null : ns;
    }

    private static FollowUpFlag? ReadFlag(JsonObject flag)
    {
        var status = Text(flag, "status") switch
        {
            "flagged" => FlagStatus.Flagged,
            "complete" => FlagStatus.Complete,
            _ => (FlagStatus?)null,
        };
        return status is null
            ? null
            : new FollowUpFlag(status.Value)
            {
                StartDateTime = ReadDate(flag["start"]),
                DueDateTime = ReadDate(flag["due"]),
                CompletedDateTime = ReadDate(flag["completed"]),
            };
    }

    private static DateTimeTimeZone? ReadDate(JsonNode? node) =>
        node is JsonObject date
        && Text(date, "dateTime") is { } text
        && DateTime.TryParseExact(text, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dateTime)
            ? new DateTimeTimeZone(dateTime, Text(date, "timeZone") ?? "UTC")
            : null;

    private static void AddDate(JsonObject json, string name, DateTimeTimeZone? value)
    {
        if (value is not null)
        {
            json[name] = new JsonObject
            {
                ["dateTime"] = value.DateTime.ToString(DateFormat, CultureInfo.InvariantCulture),
                ["timeZone"] = value.TimeZone,
            };
        }
    }

    private static string? Text(JsonObject obj, string name) =>
        obj[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

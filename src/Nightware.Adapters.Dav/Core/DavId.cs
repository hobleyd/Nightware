using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Core;

public enum DavItemKind
{
    /// <summary>A stored DAV resource (event/series master/task/contact/collection).</summary>
    Resource,
    /// <summary>One occurrence of a recurring event: master resource + RECURRENCE-ID (ADR 0001 §4).</summary>
    Occurrence,
}

/// <summary>
/// An iCalendar RECURRENCE-ID in the canonical form used inside occurrence ids:
/// <c>yyyyMMdd'T'HHmmss'Z'</c> (UTC) for timed series, <c>yyyyMMdd</c> for all-day
/// (<c>VALUE=DATE</c>) series. Sabre's <c>expand</c> emits exactly these forms.
/// </summary>
public readonly record struct DavRecurrenceId
{
    private const string UtcFormat = "yyyyMMdd'T'HHmmss'Z'";
    private const string DateFormat = "yyyyMMdd";

    private DavRecurrenceId(string value) => Value = value;

    public string Value { get; }

    public bool IsDate => Value.Length == DateFormat.Length;

    public static DavRecurrenceId FromUtc(DateTimeOffset instant) =>
        new(instant.UtcDateTime.ToString(UtcFormat, CultureInfo.InvariantCulture));

    public static DavRecurrenceId FromDate(DateOnly date) =>
        new(date.ToString(DateFormat, CultureInfo.InvariantCulture));

    public static bool TryParse(string? value, out DavRecurrenceId recurrenceId)
    {
        recurrenceId = default;
        if (value is null)
        {
            return false;
        }

        if (value.Length == DateFormat.Length
            && DateOnly.TryParseExact(value, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            recurrenceId = FromDate(date);
            return recurrenceId.Value == value;
        }

        if (value.Length == 16
            && DateTime.TryParseExact(value, UtcFormat, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc))
        {
            recurrenceId = FromUtc(new DateTimeOffset(utc, TimeSpan.Zero));
            return recurrenceId.Value == value;
        }

        return false;
    }

    /// <summary>The date for an all-day RECURRENCE-ID.</summary>
    public DateOnly ToDate() => DateOnly.ParseExact(Value[..8], DateFormat, CultureInfo.InvariantCulture);

    /// <summary>The instant; for an all-day id, midnight UTC of that date.</summary>
    public DateTimeOffset ToUtc() => IsDate
        ? new DateTimeOffset(ToDate().ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
        : new DateTimeOffset(DateTime.ParseExact(Value, UtcFormat, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal), TimeSpan.Zero);

    public override string ToString() => Value ?? string.Empty;
}

/// <summary>
/// The opaque, URL-safe id the DAV adapter emits for its objects (events,
/// occurrences, tasks, contacts, lists). <b>The format is frozen</b>:
/// <list type="bullet">
/// <item><c>base64url("r:" + href)</c> for a resource;</item>
/// <item><c>base64url("o:" + recurrenceId + ":" + href)</c> for an occurrence,</item>
/// </list>
/// where <c>href</c> is the percent-encoded path relative to the DAV root exactly as
/// Sabre returned it, and <c>recurrenceId</c> is a <see cref="DavRecurrenceId"/>.
/// The two-letter prefix is the version marker; parsing rejects anything else, any
/// href that could escape the DAV root, and non-canonical base64.
/// </summary>
public readonly record struct DavItemId
{
    public const int MaxEncodedLength = 4096;
    private const string ResourcePrefix = "r:";
    private const string OccurrencePrefix = "o:";

    private DavItemId(DavItemKind kind, string href, DavRecurrenceId? recurrenceId)
    {
        Kind = kind;
        Href = href;
        RecurrenceId = recurrenceId;
    }

    public DavItemKind Kind { get; }

    /// <summary>Href relative to the DAV root (for an occurrence: the series master's resource).</summary>
    public string Href { get; }

    /// <summary>Set for <see cref="DavItemKind.Occurrence"/>.</summary>
    public DavRecurrenceId? RecurrenceId { get; }

    public bool IsOccurrence => Kind == DavItemKind.Occurrence;

    /// <summary>The series master (or the resource itself).</summary>
    public DavItemId Master => IsOccurrence ? Resource(Href) : this;

    /// <param name="relativeHref">Href relative to the DAV root (use <see cref="DavPaths.ToRelative"/>).</param>
    public static DavItemId Resource(string relativeHref)
    {
        EnsureSafe(relativeHref);
        return new DavItemId(DavItemKind.Resource, relativeHref, null);
    }

    public static DavItemId Occurrence(string relativeHref, DavRecurrenceId recurrenceId)
    {
        EnsureSafe(relativeHref);
        if (recurrenceId.Value is null)
        {
            throw new ArgumentException("A recurrence id is required.", nameof(recurrenceId));
        }

        return new DavItemId(DavItemKind.Occurrence, relativeHref, recurrenceId);
    }

    public static bool TryParse(string? value, [NotNullWhen(true)] out DavItemId? id)
    {
        id = null;
        if (string.IsNullOrEmpty(value) || value.Length > MaxEncodedLength)
        {
            return false;
        }

        var bytes = new byte[Base64Url.GetMaxDecodedLength(value.Length)];
        if (!Base64Url.TryDecodeFromChars(value, bytes, out var written))
        {
            return false;
        }

        string payload;
        try
        {
            payload = new UTF8Encoding(false, true).GetString(bytes, 0, written);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        DavItemId candidate;
        if (payload.StartsWith(ResourcePrefix, StringComparison.Ordinal))
        {
            var href = payload[ResourcePrefix.Length..];
            if (!DavHrefRules.IsSafeRelative(href))
            {
                return false;
            }

            candidate = new DavItemId(DavItemKind.Resource, href, null);
        }
        else if (payload.StartsWith(OccurrencePrefix, StringComparison.Ordinal))
        {
            var rest = payload[OccurrencePrefix.Length..];
            var colon = rest.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0 || !DavRecurrenceId.TryParse(rest[..colon], out var recurrenceId))
            {
                return false;
            }

            var href = rest[(colon + 1)..];
            if (!DavHrefRules.IsSafeRelative(href))
            {
                return false;
            }

            candidate = new DavItemId(DavItemKind.Occurrence, href, recurrenceId);
        }
        else
        {
            return false;
        }

        // Only the canonical (unpadded) encoding is accepted, so one object has exactly one id.
        if (!string.Equals(candidate.ToString(), value, StringComparison.Ordinal))
        {
            return false;
        }

        id = candidate;
        return true;
    }

    /// <summary>Parses an id, yielding <see cref="ErrorKind.NotFound"/> for anything malformed or tampered with.</summary>
    public static Result<DavItemId> Parse(string? value, string? target = null) =>
        TryParse(value, out var id)
            ? id.Value
            : Error.NotFound("The specified object was not found in the store.", target);

    /// <summary>True when this id's href lies inside <paramref name="collectionHref"/> (relative, trailing slash).</summary>
    public bool IsWithin(string collectionHref)
    {
        ArgumentNullException.ThrowIfNull(collectionHref);
        var prefix = collectionHref.EndsWith('/') ? collectionHref : collectionHref + "/";
        return Href.StartsWith(prefix, StringComparison.Ordinal) && Href.Length > prefix.Length;
    }

    public override string ToString()
    {
        if (Href is null)
        {
            return string.Empty;
        }

        var payload = Kind == DavItemKind.Resource
            ? ResourcePrefix + Href
            : $"{OccurrencePrefix}{RecurrenceId!.Value.Value}:{Href}";
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload));
    }

    private static void EnsureSafe(string relativeHref)
    {
        if (!DavHrefRules.IsSafeRelative(relativeHref))
        {
            throw new ArgumentException("The href must be a safe path relative to the DAV root.", nameof(relativeHref));
        }
    }
}

/// <summary>Conversions between <see cref="DavItemId"/> and the Domain's typed ids.</summary>
public static class DavItemIdExtensions
{
    public static EventId ToEventId(this DavItemId id) => new(id.ToString());

    public static TaskId ToTaskId(this DavItemId id) => new(id.ToString());

    public static TaskListId ToTaskListId(this DavItemId id) => new(id.ToString());

    public static ContactId ToContactId(this DavItemId id) => new(id.ToString());

    public static Result<DavItemId> ToDavItemId(this EventId id) => DavItemId.Parse(id.Value, "id");

    public static Result<DavItemId> ToDavItemId(this TaskId id) => DavItemId.Parse(id.Value, "id");

    public static Result<DavItemId> ToDavItemId(this TaskListId id) => DavItemId.Parse(id.Value, "id");

    public static Result<DavItemId> ToDavItemId(this ContactId id) => DavItemId.Parse(id.Value, "id");
}

using Nightware.Application.Common.Time;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Calendar.ICal;

/// <summary>Context for parsing a stored or expanded calendar resource (beyond the port's plain <c>Parse</c>).</summary>
public sealed record CalendarParseOptions
{
    public static CalendarParseOptions Default { get; } = new();

    /// <summary>
    /// The resource's href relative to the DAV root. When set, events get real ids:
    /// masters/single events <c>DavItemId.Resource(href)</c>; exceptions, cancelled
    /// occurrences and expanded instances <c>DavItemId.Occurrence(href, RECURRENCE-ID)</c>
    /// with <c>SeriesMasterId</c> set. When null, ids are empty placeholders
    /// (callers assign them with <c>with { Id = … }</c>).
    /// </summary>
    public string? Href { get; init; }

    /// <summary>The mailbox reading the event, used for <c>IsOrganizer</c> and <c>ResponseStatus</c>.</summary>
    public MailboxRef? Mailbox { get; init; }

    /// <summary>Zone used for floating (zone-less) date-times. Domain has no "floating"; default UTC.</summary>
    public string FloatingTimeZone { get; init; } = GraphTimeZone.Utc;

    /// <summary>
    /// True for <c>calendar-query</c> results produced with <c>C:expand</c>: Sabre writes every
    /// instance as a VEVENT with RECURRENCE-ID, so those are typed <c>Occurrence</c>, not <c>Exception</c>.
    /// </summary>
    public bool Expanded { get; init; }
}

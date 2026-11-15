using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using Ical.Net.Evaluation;
using Nightware.Adapters.Dav.Core;
using Nightware.Domain.Kernel;
using IcalCalendar = Ical.Net.Calendar;
using IcalEvent = Ical.Net.CalendarComponents.CalendarEvent;

namespace Nightware.Adapters.Dav.Calendar.ICal;

// Occurrence ids are first-class write targets (ADR 0001 §4): an edit of "this occurrence only" needs a
// component of its own, the RFC 5545 exception VEVENT (same UID, RECURRENCE-ID = the original start).
public sealed partial class IcsEditor
{
    private static readonly string[] RecurrenceProperties = ["RRULE", "RDATE", "EXDATE", "EXRULE"];

    /// <summary>
    /// The calendar with <paramref name="occurrence"/> given its own exception component when it had none,
    /// so a reader can parse the unmodified occurrence like any other instance. NotFound when the series
    /// does not produce that occurrence (wrong time, excluded by EXDATE, or not a series at all).
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822", Justification = "Instance API of a DI-registered editor.")]
    public Result<string> MaterializeOccurrence(string ics, DavRecurrenceId occurrence) =>
        Edit(ics, calendar => EnsureException(calendar, new IcalZoneContext(calendar, "UTC"), occurrence).ToResult());

    /// <summary>
    /// The exception component for <paramref name="occurrence"/>, created from the master when the
    /// occurrence is unmodified. The copy keeps every property of the master (attendees with their
    /// SCHEDULE-* parameters, X- properties, alarms) minus the recurrence itself, starts at the
    /// occurrence in the master's own DTSTART form and lasts as long as the master.
    /// </summary>
    private static Result<IcalEvent> EnsureException(IcalCalendar calendar, IcalZoneContext zones, DavRecurrenceId occurrence)
    {
        var existing = calendar.Events.FirstOrDefault(e => e.RecurrenceIdentifier?.StartTime is { } rid && SameInstance(zones, rid, occurrence));
        if (existing is not null)
        {
            return existing;
        }

        var master = Master(calendar);
        if (master?.DtStart is null || (master.RecurrenceRule is null && master.RecurrenceDates.GetAllDates().FirstOrDefault() is null))
        {
            return Error.NotFound("The event is not a recurring series.");
        }

        var start = InstanceValue(master, zones, occurrence);
        if (!ProducesInstance(master, zones, start))
        {
            return Error.NotFound("The series has no occurrence at that time.");
        }

        if (DeepCopy(master) is not { } exception)
        {
            return Error.NotFound("The series could not be copied into an occurrence.");
        }

        foreach (var name in RecurrenceProperties)
        {
            exception.Properties.Remove(name);
        }

        exception.RecurrenceIdentifier = new RecurrenceIdentifier(start, null);
        exception.DtStart = start;
        if (master.DtEnd is { } masterEnd)
        {
            exception.DtEnd = start.HasTime
                ? ShiftTimed(start, zones.ToInstant(masterEnd) - zones.ToInstant(master.DtStart), zones)
                : start.AddDays(masterEnd.Date.DayNumber - master.DtStart.Date.DayNumber);
        }

        calendar.Events.Add(exception);
        return exception;
    }

    /// <summary>
    /// An independent copy of a component. Ical.Net's <c>Copy</c> shares the property values (an attendee
    /// answered on the copy would answer on the master too), so the copy goes through the serializer.
    /// </summary>
    private static IcalEvent? DeepCopy(IcalEvent component)
    {
        var wrapper = new IcalCalendar();
        wrapper.Events.Add(component.Copy<IcalEvent>()!);
        var reloaded = IcalNetCalendarCodec.Load(new CalendarSerializer().SerializeToString(wrapper));
        if (!reloaded.IsSuccess || reloaded.Value.Events.FirstOrDefault() is not { } copy)
        {
            return null;
        }

        reloaded.Value.Children.Remove(copy);
        return copy;
    }

    /// <summary>
    /// The occurrence's start written the way the master writes DTSTART (a date for all-day series, the
    /// master's zone when it can be resolved, otherwise UTC), as RFC 5545 §3.8.4.4 asks of RECURRENCE-ID and EXDATE.
    /// </summary>
    private static CalDateTime InstanceValue(IcalEvent master, IcalZoneContext zones, DavRecurrenceId occurrence)
    {
        var instant = occurrence.ToUtc();
        if (!master.DtStart!.HasTime)
        {
            return new CalDateTime(occurrence.IsDate ? occurrence.ToDate() : DateOnly.FromDateTime(instant.UtcDateTime));
        }

        if (master.DtStart is { IsUtc: false, IsFloating: false } zoned && zones.ZoneNameOf(zoned) is { } zoneName)
        {
            return new CalDateTime(IcalZoneContext.ToWallClock(instant, zoneName), zoned.TzId, true);
        }

        // UTC, floating, or a TZID we cannot resolve (e.g. Outlook display names): UTC is always exact.
        return IcalNetCalendarCodec.Utc(instant);
    }

    /// <summary>True when the master's recurrence set (RRULE/RDATE minus EXDATE) contains <paramref name="start"/>.</summary>
    private static bool ProducesInstance(IcalEvent master, IcalZoneContext zones, CalDateTime start)
    {
        var wanted = zones.ToInstant(start);
        var from = start.HasTime ? start.AddSeconds(-1) : start.AddDays(-1);
        try
        {
            foreach (var instance in master.GetOccurrences(from, new EvaluationOptions()))
            {
                var at = zones.ToInstant(instance.Period.StartTime);
                if (at == wanted)
                {
                    return true;
                }

                if (at > wanted)
                {
                    return false;
                }
            }
        }
        catch (EvaluationException)
        {
            return false;
        }

        return false;
    }

    /// <summary>A timed value <paramref name="duration"/> later, in the same zone form as <paramref name="start"/>.</summary>
    private static CalDateTime ShiftTimed(CalDateTime start, TimeSpan duration, IcalZoneContext zones)
    {
        var end = zones.ToInstant(start) + duration;
        if (start is { IsUtc: false, IsFloating: false } zoned && zones.ZoneNameOf(zoned) is { } zoneName)
        {
            return new CalDateTime(IcalZoneContext.ToWallClock(end, zoneName), zoned.TzId, true);
        }

        return start.IsFloating ? start.Add(Duration.FromTimeSpanExact(duration)) : IcalNetCalendarCodec.Utc(end);
    }
}

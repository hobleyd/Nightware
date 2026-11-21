using Nightware.Application.Calendar;
using Nightware.Application.Calendar.Calendars;
using Nightware.Application.Common.Query;
using Nightware.Conformance.Tests.Harness;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Conformance.Tests.Arrangements;

/// <summary>Phase 13 reminders (ADR 0006 D6), against captures from Microsoft 365.</summary>
public sealed class ReminderArrangements : IConformanceArrangements
{
    private static readonly MailboxRef Me = new("me@example.com");
    private static readonly EventId Single = new("AAMk-id-0010=");

    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        yield return new ConformanceArrangement("calendar.reminder-view", static fakes =>
            fakes.For<ICalendarReader>().CalendarViewAsync(Me, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>())
                .Returns(new Page<CalendarEvent>(
                [
                    new CalendarEvent(Single, "Nightware capture ae6347 single",
                        new DateTimeTimeZone(new DateTime(2026, 11, 20, 23, 38, 0), "UTC"), new DateTimeTimeZone(new DateTime(2026, 11, 21, 0, 8, 0), "UTC"))
                    {
                        IsReminderOn = true,
                        ReminderMinutesBeforeStart = 15,
                    },
                    new CalendarEvent(new EventId("AAMk-id-0006="), "Nightware capture ae6347 series",
                        new DateTimeTimeZone(new DateTime(2026, 11, 21, 10, 0, 0), "UTC"), new DateTimeTimeZone(new DateTime(2026, 11, 21, 10, 30, 0), "UTC"))
                    {
                        IsReminderOn = true,
                        ReminderMinutesBeforeStart = 15,
                        SeriesMasterId = new EventId("AAMk-id-0005="),
                    },
                ])))
        {
            // Exchange adds a change key, an Outlook link and a full (empty) location; compared on the rest.
            Normalise = ParityNormalisers.SharedMembers,
        };

        yield return new ConformanceArrangement("calendar.events.snooze-reminder", static fakes =>
            fakes.For<ICalendarReminders>().SnoozeAsync(Me, Single, new DateTimeOffset(2026, 11, 20, 23, 8, 0, TimeSpan.Zero), Arg.Any<CancellationToken>())
                .Returns(Result.Success()));

        yield return new ConformanceArrangement("calendar.events.dismiss-reminder", static fakes =>
            fakes.For<ICalendarReminders>().DismissAsync(Me, Single, Arg.Any<CancellationToken>()).Returns(Result.Success()));
    }
}

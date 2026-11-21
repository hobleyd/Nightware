using Nightware.Application.Calendar.Calendars;
using Nightware.Application.Common.Query;
using Nightware.Conformance.Tests.Harness;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Conformance.Tests.Arrangements;

/// <summary>
/// Phase 13 calendars and calendar groups (ADR 0006 D6), against captures from Microsoft 365. Calendars compare on
/// the members both sides emit; events compare on id, subject, start and end (the event DTO is NightMail's).
/// </summary>
public sealed class CalendarCollectionArrangements : IConformanceArrangements
{
    private static readonly MailboxRef Me = new("me@example.com");
    private static readonly EmailAddress Owner = new("colleague@example.com", "Me Example");
    private static readonly CalendarId Created = new("AAMk-id-0004=");

    private static readonly EventCalendar[] Tenant =
    [
        new(new CalendarId("AAMk-id-0001="), "Calendar") { IsDefault = true, CanEdit = true, Owner = Owner },
        new(new CalendarId("AAMk-id-0002="), "United States holidays") { IsRemovable = true, Owner = Owner },
        new(new CalendarId("AAMk-id-0003="), "Birthdays") { IsRemovable = true, Owner = Owner },
    ];

    private static readonly EventCalendar Nightware = new(Created, "Nightware ae6347") { CanEdit = true, IsRemovable = true, Owner = Owner };
    private static readonly EventCalendar Renamed = Nightware with { Name = "Nightware ae6347 renamed", HexColor = "#87d28e" };

    /// <summary>Exchange-only: change keys, scrubbed class ids, Teams settings, response tallying.</summary>
    private static readonly IReadOnlyList<string> ExchangeOnly = ["changeKey", "groupClassId", "allowedOnlineMeetingProviders", "defaultOnlineMeetingProvider", "isTallyingResponses", "canShare"];

    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        yield return Stored("calendar.calendars.list", [.. Tenant]);
        yield return Stored("calendar.calendar.get", [.. Tenant]);
        yield return Stored("calendar.calendars.get", [.. Tenant, Nightware]);
        yield return Stored("calendar.calendar-groups.calendars", [.. Tenant, Renamed]);

        yield return new ConformanceArrangement("calendar.calendars.create", static fakes =>
            fakes.For<ICalendarStore>().CreateAsync(Me, "Nightware ae6347", null, Arg.Any<CancellationToken>()).Returns(Nightware))
        {
            IgnorePaths = Prefixed(ExchangeOnly),
        };

        yield return new ConformanceArrangement("calendar.calendars.update", static fakes =>
            fakes.For<ICalendarStore>().UpdateAsync(Me, Created, "Nightware ae6347 renamed", "#87D28E", Arg.Any<CancellationToken>()).Returns(Renamed))
        {
            IgnorePaths = Prefixed(ExchangeOnly),
        };

        yield return new ConformanceArrangement("calendar.calendars.delete", static fakes =>
        {
            fakes.For<ICalendarStore>().ListAsync(Me, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<EventCalendar>>.Success([.. Tenant, Renamed]));
            fakes.For<ICalendarStore>().DeleteAsync(Me, Created, Arg.Any<CancellationToken>()).Returns(Result.Success());
        });

        // Graph has three groups; Nightware one ("My Calendars", ADR 0006 D6), compared by name.
        yield return new ConformanceArrangement("calendar.calendar-groups.list", static _ => { })
        {
            Normalise = static (expected, actual) =>
            {
                var first = expected["value"]!.AsArray()[0]!.DeepClone();
                expected["value"] = new System.Text.Json.Nodes.JsonArray(first);
                ParityNormalisers.Only("name")(expected, actual);
            },
        };

        yield return new ConformanceArrangement("calendar.calendars.events.list", static fakes =>
            fakes.For<ICalendarEventsIn>().ListAsync(Me, Created, Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>())
                .Returns(new Page<CalendarEvent>([Series("AAMk-id-0005=", 2026, 11, 21)])))
        {
            Normalise = ParityNormalisers.Only("id", "subject", "start", "end"),
        };

        yield return new ConformanceArrangement("calendar.calendars.events.create", static fakes =>
            fakes.For<ICalendarEventsIn>().CreateAsync(Me, Created, Arg.Is<NewEvent>(e => e.Subject == "Nightware capture ae6347 series" && e.Recurrence != null), Arg.Any<CancellationToken>())
                .Returns(Series("AAMk-id-0005=", 2026, 11, 21)))
        {
            Normalise = ParityNormalisers.Only("id", "subject", "start", "end"),
        };

        yield return new ConformanceArrangement("calendar.calendars.calendar-view", static fakes =>
            fakes.For<ICalendarEventsIn>().CalendarViewAsync(Me, Created, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>())
                .Returns(new Page<CalendarEvent>(
                [
                    Series("AAMk-id-0006=", 2026, 11, 21), Series("AAMk-id-0007=", 2026, 11, 28),
                    Series("AAMk-id-0008=", 2026, 12, 5), Series("AAMk-id-0009=", 2026, 12, 12),
                ])))
        {
            Normalise = ParityNormalisers.Only("id", "subject", "start", "end"),
        };
    }

    private static ConformanceArrangement Stored(string operationId, EventCalendar[] calendars) =>
        new(operationId, fakes => fakes.For<ICalendarStore>().ListAsync(Me, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<EventCalendar>>.Success(calendars)))
        {
            IgnorePaths = operationId.EndsWith(".get", StringComparison.Ordinal) ? Prefixed(ExchangeOnly) : [.. ExchangeOnly.Select(static m => "value/*/" + m)],
        };

    private static string[] Prefixed(IEnumerable<string> members) => [.. members];

    private static CalendarEvent Series(string id, int year, int month, int day) =>
        new(new EventId(id), "Nightware capture ae6347 series",
            new DateTimeTimeZone(new DateTime(year, month, day, 10, 0, 0), "UTC"),
            new DateTimeTimeZone(new DateTime(year, month, day, 10, 30, 0), "UTC"));
}

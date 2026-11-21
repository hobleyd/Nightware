using Nightware.Application.Calendar.Calendars;
using Nightware.Conformance.Tests.Harness;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Conformance.Tests.Arrangements;

/// <summary>
/// Phase 18 aliases: Graph's other routes to an operation Nightware already has. Each is arranged exactly as its base
/// operation, so a pass shows the alias reaches the same handling.
/// </summary>
public sealed class AliasArrangements : IConformanceArrangements
{
    private static readonly MailboxRef Me = new("me@example.com");

    /// <summary>The created calendar (calendars/AAMk-id-0004=) as the default, for the calendar/… aliases.</summary>
    private static void DefaultIsCreated(PortFakes fakes) =>
        fakes.For<ICalendarStore>().ListAsync(Me, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<EventCalendar>>.Success(
            [new EventCalendar(new CalendarId("AAMk-id-0004="), "Nightware ae6347") { IsDefault = true, CanEdit = true }]));

    private static readonly (string Alias, string Base, bool Default)[] Aliases =
    [
        ("calendar.default.events.list", "calendar.calendars.events.list", true),
        ("calendar.default.events.create", "calendar.calendars.events.create", true),
        ("calendar.default.calendar-view", "calendar.calendars.calendar-view", true),
        ("calendar.default.events.get", "calendar.events.get", false),
        ("calendar.default.events.update", "calendar.events.update", false),
        ("calendar.default.events.delete", "calendar.events.delete", false),
        ("calendar.calendars.events.get", "calendar.events.get", false),
        ("calendar.calendars.events.update", "calendar.events.update", false),
        ("calendar.calendars.events.delete", "calendar.events.delete", false),
        ("mail.folders.messages.get", "mail.messages.get", false),
        ("mail.folders.messages.update", "mail.messages.update-read-state", false),
        ("mail.folders.messages.delete", "mail.messages.delete", false),
        ("contacts.folders.contacts.get", "contacts.get", false),
        ("contacts.folders.contacts.update", "contacts.update", false),
        ("contacts.folders.contacts.delete", "contacts.delete", false),
    ];

    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        // The other providers directly: the registry discovers this one, so it can't be asked from here.
        IConformanceArrangements[] providers =
            [new CalendarArrangements(), new CalendarCollectionArrangements(), new MailReadArrangements(), new MailWriteArrangements(), new ContactParityArrangements()];
        var bases = providers.SelectMany(static p => p.Arrangements()).ToDictionary(static a => a.OperationId, StringComparer.Ordinal);
        foreach (var (alias, baseId, isDefault) in Aliases)
        {
            var original = bases[baseId];
            yield return original with
            {
                OperationId = alias,
                Arrange = isDefault ? fakes => { original.Arrange(fakes); DefaultIsCreated(fakes); } : original.Arrange,
            };
        }
    }
}

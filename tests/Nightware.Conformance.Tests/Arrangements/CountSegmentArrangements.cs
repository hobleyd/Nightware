using Nightware.Application.Calendar;
using Nightware.Application.Calendar.Calendars;
using Nightware.Application.Common.Query;
using Nightware.Application.Contacts;
using Nightware.Application.Mail;
using Nightware.Application.Tasks;
using Nightware.Conformance.Tests.Harness;
using Nightware.Domain.Calendar;
using Nightware.Domain.Contacts;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;
using Nightware.Domain.Tasks;

namespace Nightware.Conformance.Tests.Arrangements;

/// <summary>
/// Phase 19: each list's <c>/$count</c> segment. The port is asked with <c>$count=true</c> (and the fixture's filter, where
/// it has one) and reports a total; the page's items don't matter, only the total is written.
/// </summary>
public sealed class CountSegmentArrangements : IConformanceArrangements
{
    private static readonly MailboxRef Me = new("me@example.com");
    private const string CalendarId = "AAMk-id-0004=";

    private static ItemQuery Counting() => Arg.Is<ItemQuery>(q => q.Count && q.Filter == null);

    private static ItemQuery CountingFiltered() => Arg.Is<ItemQuery>(q => q.Count && q.Filter != null);

    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        yield return new ConformanceArrangement("mail.messages.count", static fakes =>
            fakes.For<IMailMessageReader>().ListAsync(Me, null, Counting(), Arg.Any<CancellationToken>())
                .Returns(new Page<MailMessage>([]) { Total = 41 }));

        yield return new ConformanceArrangement("mail.folders.messages.count", static fakes =>
            fakes.For<IMailMessageReader>().ListAsync(Me, new FolderRef.WellKnown(WellKnownFolder.Inbox), CountingFiltered(), Arg.Any<CancellationToken>())
                .Returns(new Page<MailMessage>([]) { Total = 3 }));

        yield return new ConformanceArrangement("calendar.events.count", static fakes =>
            fakes.For<ICalendarReader>().ListAsync(Me, Counting(), Arg.Any<CancellationToken>())
                .Returns(new Page<CalendarEvent>([]) { Total = 12 }));

        yield return new ConformanceArrangement("calendar.default.events.count", static fakes =>
        {
            fakes.For<ICalendarStore>().ListAsync(Me, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<EventCalendar>>.Success(
                [new EventCalendar(new CalendarId(CalendarId), "Calendar") { IsDefault = true, CanEdit = true }]));
            fakes.For<ICalendarEventsIn>().ListAsync(Me, new CalendarId(CalendarId), Counting(), Arg.Any<CancellationToken>())
                .Returns(new Page<CalendarEvent>([]) { Total = 12 });
        });

        yield return new ConformanceArrangement("calendar.calendars.events.count", static fakes =>
            fakes.For<ICalendarEventsIn>().ListAsync(Me, new CalendarId(CalendarId), Counting(), Arg.Any<CancellationToken>())
                .Returns(new Page<CalendarEvent>([]) { Total = 7 }));

        yield return new ConformanceArrangement("contacts.count", static fakes =>
            fakes.For<IContactStore>().ListAsync(Me, Counting(), Arg.Any<CancellationToken>())
                .Returns(new Page<Contact>([]) { Total = 250 }));

        yield return new ConformanceArrangement("contacts.folders.contacts.count", static fakes =>
        {
            var folder = new ContactFolderId("AAMk-id-0002=");
            fakes.For<IContactFolderStore>().GetAsync(Me, folder.Value, Arg.Any<CancellationToken>()).Returns(new ContactFolder(folder, "Friends", null));
            fakes.For<IContactStore>().ListInAsync(Me, folder, Counting(), Arg.Any<CancellationToken>()).Returns(new Page<Contact>([]) { Total = 0 });
        });

        yield return new ConformanceArrangement("tasks.tasks.count", static fakes =>
            fakes.For<ITaskStore>().ListAsync(Me, new TaskListId(CalendarId), CountingFiltered(), Arg.Any<CancellationToken>())
                .Returns(new Page<TodoTask>([]) { Total = 5 }));
    }
}

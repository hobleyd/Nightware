using System.Text;
using Nightware.Application.Calendar;
using Nightware.Application.Common.Query;
using Nightware.Application.Mail;
using Nightware.Conformance.Tests.Harness;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Conformance.Tests.Arrangements;

/// <summary>Phase 4: calendar view, events, meeting actions, invitation mail → event, and free/busy.</summary>
public sealed class CalendarArrangements : IConformanceArrangements
{
    private const string EventIdValue = "AAMkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OABGAAAAAAAiQ8W967B7TKBjgx9rVEURBwAiIsqMbYjsT5e-T7KzowPTAAAAAAENAAAiIsqMbYjsT5e-T7KzowPTAAAYbwXuAAA=";
    private const string OccurrenceIdValue = "AAMkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OAFRAAgI3sK1W2_AAEYAAAAAIkPFveuwe0ygY4Mfa1RFEQcAIiLKjG2I7E-Xvk-ys6MD0wAAAAABDQAAIiLKjG2I7E-Xvk-ys6MD0wAAGG8F7gAAEA==";
    private const string MessageIdValue = "AAMkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OABGAAAAAAAiQ8W967B7TKBjgx9rVEURBwAiIsqMbYjsT5e-T7KzowPTAAAAAAEMAAAiIsqMbYjsT5e-T7KzowPTAAAYbvZDAAA=";
    private const string Uid = "040000008200E00074C5B7101A82E00800000000A1B2C3D4E5F6D901000000000000000010000000F1E2D3C4B5A69788";

    private static readonly MailboxRef Me = new("me@example.com");
    private static readonly EventId EventIdOf = new(EventIdValue);
    private static readonly MessageId MessageIdOf = new(MessageIdValue);

    /// <summary>
    /// Every member of the default event projection other than the two the linked-event fixtures pin.
    /// The eventMessageResponse spelling is sent without <c>$select</c>, so the façade answers with the full
    /// event (as Graph does); its fixture was trimmed to the members NightMail reads.
    /// </summary>
    private static readonly string[] EventMembersBeyondIdAndOrganizer =
    [
        "iCalUId", "subject", "bodyPreview", "isAllDay", "showAs", "isReminderOn", "reminderMinutesBeforeStart", "seriesMasterId",
        "responseStatus", "start", "end", "location", "recurrence", "attendees", "organizer", "onlineMeeting",
    ];

    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        yield return new ConformanceArrangement("calendar.calendar-view.list", static fakes =>
            fakes.For<ICalendarReader>()
                .CalendarViewAsync(Me, Utc(2026, 6, 1), Utc(2026, 7, 1), Arg.Is<ItemQuery>(q => q.Top == 100), Arg.Any<CancellationToken>())
                .Returns(new Page<CalendarEvent>([QuarterlyReview(), WeeklySyncOccurrence()])));

        yield return new ConformanceArrangement("calendar.events.get", static fakes =>
            fakes.For<ICalendarReader>()
                .GetAsync(Me, EventIdOf, Arg.Any<CancellationToken>())
                .Returns(WeeklySyncMaster()));

        yield return new ConformanceArrangement("calendar.events.list-by-ical-uid", static fakes =>
            fakes.For<ICalendarReader>()
                .ListAsync(Me, Arg.Is<ItemQuery>(q => q.Top == 5 && IsUidFilter(q.Filter)), Arg.Any<CancellationToken>())
                .Returns(new Page<CalendarEvent>([QuarterlyReview()])));

        yield return new ConformanceArrangement("calendar.events.create", static fakes =>
            fakes.For<ICalendarWriter>()
                .CreateAsync(Me, Arg.Is<NewEvent>(n => n.Start == new DateTimeTimeZone(new DateTime(2026, 6, 10, 19, 0, 0), "Australia/Sydney")
                                                     && n.Attendees.Count == 2 && n.Location!.LocationEmailAddress == "boardroom@example.com"), Arg.Any<CancellationToken>())
                .Returns(QuarterlyReview() with
                {
                    Start = new DateTimeTimeZone(new DateTime(2026, 6, 10, 19, 0, 0), "Australia/Sydney"),
                    End = new DateTimeTimeZone(new DateTime(2026, 6, 10, 20, 0, 0), "Australia/Sydney"),
                    Attendees =
                    [
                        new Attendee(new EmailAddress("sam@example.com"), AttendeeType.Required, new ResponseStatus(ResponseType.None)),
                        new Attendee(new EmailAddress("boardroom@example.com", "Boardroom"), AttendeeType.Resource, new ResponseStatus(ResponseType.None)),
                    ],
                    OnlineMeetingUrl = null,
                    IsOnlineMeeting = false,
                }));

        yield return new ConformanceArrangement("calendar.events.update", static fakes =>
            fakes.For<ICalendarWriter>()
                .UpdateAsync(Me, EventIdOf, Arg.Is<EventPatch>(p => p.Start == DateTimeTimeZone.Utc(new DateTimeOffset(2026, 6, 10, 10, 0, 0, TimeSpan.Zero))
                                                                   && p.End == DateTimeTimeZone.Utc(new DateTimeOffset(2026, 6, 10, 11, 0, 0, TimeSpan.Zero))
                                                                   && p.Subject == null && p.Attendees == null), Arg.Any<CancellationToken>())
                .Returns(QuarterlyReview() with
                {
                    Start = DateTimeTimeZone.Utc(new DateTimeOffset(2026, 6, 10, 10, 0, 0, TimeSpan.Zero)),
                    End = DateTimeTimeZone.Utc(new DateTimeOffset(2026, 6, 10, 11, 0, 0, TimeSpan.Zero)),
                }));

        yield return new ConformanceArrangement("calendar.events.delete", static fakes =>
            fakes.For<ICalendarWriter>().DeleteAsync(Me, EventIdOf, Arg.Any<CancellationToken>()).Returns(Result.Success()));

        yield return Rsvp("calendar.events.accept", RsvpResponse.Accept, "See you there");
        yield return Rsvp("calendar.events.tentatively-accept", RsvpResponse.TentativelyAccept, "See you there");

        yield return new ConformanceArrangement("calendar.events.decline", static fakes =>
            fakes.For<ISchedulingActions>()
                .RespondAsync(Me, EventIdOf, RsvpResponse.Decline, null, true,
                    new TimeSlot(new DateTimeTimeZone(new DateTime(2026, 6, 10, 11, 30, 0), "Australia/Sydney"), new DateTimeTimeZone(new DateTime(2026, 6, 10, 12, 30, 0), "Australia/Sydney")),
                    Arg.Any<CancellationToken>())
                .Returns(Result<ItipMessage?>.Success(null)));

        yield return MessageRsvp("calendar.messages.accept", RsvpResponse.Accept);
        yield return MessageRsvp("calendar.messages.tentatively-accept", RsvpResponse.TentativelyAccept);
        yield return MessageRsvp("calendar.messages.decline", RsvpResponse.Decline);

        yield return new ConformanceArrangement("calendar.events.cancel", static fakes =>
            fakes.For<ISchedulingActions>().CancelAsync(Me, EventIdOf, null, Arg.Any<CancellationToken>()).Returns(Result.Success()));

        yield return new ConformanceArrangement("calendar.events.forward", static fakes =>
            fakes.For<ISchedulingActions>()
                .ForwardAsync(Me, EventIdOf, Arg.Is<IReadOnlyList<EmailAddress>>(r => r.Count == 1 && r[0].Address == "pat@example.com"), "Can you join?", Arg.Any<CancellationToken>())
                .Returns(Result.Success()));

        yield return LinkedEvent("calendar.messages.linked-event", isOrganizer: false);
        yield return LinkedEvent("calendar.messages.linked-event-request-cast", isOrganizer: false);
        yield return LinkedEvent("calendar.messages.linked-event-uncast", isOrganizer: false);
        yield return LinkedEvent("calendar.messages.linked-event-response-cast", isOrganizer: true) with
        {
            IgnorePaths = [.. EventMembersBeyondIdAndOrganizer],
        };

        yield return new ConformanceArrangement("calendar.get-schedule", static fakes =>
            fakes.For<IFreeBusyReader>()
                .GetAsync(Me, Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "sam@example.com", "boardroom@example.com" })), Utc(2026, 6, 10), Utc(2026, 6, 10).AddHours(8), Arg.Any<CancellationToken>())
                .Returns(Result<IReadOnlyList<FreeBusySchedule>>.Success(
                [
                    new FreeBusySchedule("sam@example.com", [new ScheduleItem(Utc(2026, 6, 10).AddHours(2), Utc(2026, 6, 10).AddHours(3), FreeBusyStatus.Busy) { Subject = "Customer call", Location = string.Empty }]),
                    new FreeBusySchedule("boardroom@example.com", []),
                ])))
        {
            // Graph adds each schedule's working hours; the façade has no source for other people's, and NightMail does not read them.
            IgnorePaths = ["value/*/workingHours"],
        };
    }

    private static ConformanceArrangement Rsvp(string operationId, RsvpResponse response, string comment) =>
        new(operationId, fakes =>
            fakes.For<ISchedulingActions>()
                .RespondAsync(Me, EventIdOf, response, comment, true, null, Arg.Any<CancellationToken>())
                .Returns(Result<ItipMessage?>.Success(null)));

    private static ConformanceArrangement MessageRsvp(string operationId, RsvpResponse response) =>
        new(operationId, fakes =>
        {
            ArrangeInvitationMail(fakes);
            ArrangeUidLookup(fakes, isOrganizer: false);
            fakes.For<ISchedulingActions>()
                .RespondAsync(Me, EventIdOf, response, null, true, null, Arg.Any<CancellationToken>())
                .Returns(Result<ItipMessage?>.Success(null));
        });

    private static ConformanceArrangement LinkedEvent(string operationId, bool isOrganizer) =>
        new(operationId, fakes =>
        {
            ArrangeInvitationMail(fakes);
            ArrangeUidLookup(fakes, isOrganizer);
        });

    /// <summary>The mail port holds an invitation whose <c>text/calendar</c> part carries the meeting's UID; the real codec parses it.</summary>
    private static void ArrangeInvitationMail(PortFakes fakes)
    {
        var mail = fakes.For<IMailMessageReader>();
        var part = new AttachmentId("2");
        mail.GetAsync(Me, MessageIdOf, Arg.Any<MessageFetch>(), Arg.Any<CancellationToken>())
            .Returns(new MailMessage(MessageIdOf) { Subject = "Quarterly review", CalendarPart = new CalendarPart("REQUEST", part) });
        var ics = Encoding.UTF8.GetBytes(
            "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Example//EN\r\nMETHOD:REQUEST\r\nBEGIN:VEVENT\r\n" +
            $"UID:{Uid}\r\nDTSTAMP:20260601T000000Z\r\nDTSTART:20260610T090000Z\r\nDTEND:20260610T100000Z\r\nSUMMARY:Quarterly review\r\n" +
            "ORGANIZER:mailto:alex@example.com\r\nATTENDEE;PARTSTAT=NEEDS-ACTION;RSVP=TRUE:mailto:me@example.com\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");
        mail.GetAttachmentContentAsync(Me, MessageIdOf, part, Arg.Any<CancellationToken>())
            .Returns(new BlobContent("text/calendar", ics.Length, _ => Task.FromResult<Stream>(new MemoryStream(ics))));
    }

    private static void ArrangeUidLookup(PortFakes fakes, bool isOrganizer) =>
        fakes.For<ICalendarReader>()
            .ListAsync(Me, Arg.Is<ItemQuery>(q => IsUidFilter(q.Filter)), Arg.Any<CancellationToken>())
            .Returns(new Page<CalendarEvent>([QuarterlyReview() with { IsOrganizer = isOrganizer }]));

    private static bool IsUidFilter(FilterNode? filter) =>
        filter is FilterNode.Compare { Property: "iCalUId", Op: CompareOp.Eq, Value: FilterValue.Text { Value: Uid } };

    private static DateTimeOffset Utc(int year, int month, int day) => new(year, month, day, 0, 0, 0, TimeSpan.Zero);

    private static CalendarEvent QuarterlyReview() =>
        new(EventIdOf, "Quarterly review", DateTimeTimeZone.Utc(new DateTimeOffset(2026, 6, 10, 9, 0, 0, TimeSpan.Zero)), DateTimeTimeZone.Utc(new DateTimeOffset(2026, 6, 10, 10, 0, 0, TimeSpan.Zero)))
        {
            ICalUId = Uid,
            BodyPreview = "Agenda: Q2 numbers.",
            ShowAs = ShowAs.Busy,
            IsOrganizer = true,
            IsReminderOn = true,
            ReminderMinutesBeforeStart = 15,
            ResponseStatus = new ResponseStatus(ResponseType.Organizer),
            Location = new Location("Boardroom") { LocationEmailAddress = "boardroom@example.com" },
            Attendees =
            [
                new Attendee(new EmailAddress("sam@example.com", "Sam Attendee"), AttendeeType.Required, new ResponseStatus(ResponseType.Accepted, new DateTimeOffset(2026, 6, 2, 1, 0, 0, TimeSpan.Zero))),
                new Attendee(new EmailAddress("boardroom@example.com", "Boardroom"), AttendeeType.Resource, new ResponseStatus(ResponseType.Accepted, new DateTimeOffset(2026, 6, 2, 1, 0, 5, TimeSpan.Zero))),
            ],
            Organizer = new EmailAddress("me@example.com", "Me Example"),
            IsOnlineMeeting = true,
            OnlineMeetingUrl = "https://teams.example.com/l/meetup-join/19%3ameeting_abc",
        };

    private static CalendarEvent WeeklySyncOccurrence() =>
        new(new EventId(OccurrenceIdValue), "Weekly sync", DateTimeTimeZone.Utc(new DateTimeOffset(2026, 6, 11, 23, 0, 0, TimeSpan.Zero)), DateTimeTimeZone.Utc(new DateTimeOffset(2026, 6, 11, 23, 30, 0, TimeSpan.Zero)))
        {
            ICalUId = Uid,
            Type = EventType.Occurrence,
            SeriesMasterId = EventIdOf,
            OriginalStart = new DateTimeOffset(2026, 6, 11, 23, 0, 0, TimeSpan.Zero),
            BodyPreview = "Agenda: Q2 numbers.",
            ShowAs = ShowAs.Busy,
            IsReminderOn = true,
            ReminderMinutesBeforeStart = 15,
            ResponseStatus = new ResponseStatus(ResponseType.NotResponded),
            Organizer = new EmailAddress("alex@example.com", "Alex Organiser"),
        };

    private static CalendarEvent WeeklySyncMaster() =>
        WeeklySyncOccurrence() with
        {
            Id = EventIdOf,
            Type = EventType.SeriesMaster,
            SeriesMasterId = null,
            OriginalStart = null,
            Start = new DateTimeTimeZone(new DateTime(2026, 6, 12, 9, 0, 0), "Australia/Sydney"),
            End = new DateTimeTimeZone(new DateTime(2026, 6, 12, 9, 30, 0), "Australia/Sydney"),
            Recurrence = new PatternedRecurrence(
                new RecurrencePattern(RecurrencePatternType.Weekly, 1) { DaysOfWeek = [DayOfWeek.Thursday] },
                new RecurrenceRange(RecurrenceRangeType.EndDate, new DateOnly(2026, 6, 11)) { EndDate = new DateOnly(2026, 12, 31), RecurrenceTimeZone = "Australia/Sydney" }),
        };
}

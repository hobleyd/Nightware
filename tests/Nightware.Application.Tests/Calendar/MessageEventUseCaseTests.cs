using System.Text;
using Nightware.Application.Calendar;
using Nightware.Application.Calendar.Messages;
using Nightware.Application.Calendar.Scheduling;
using Nightware.Application.Common.Query;
using Nightware.Application.Mail;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Tests.Calendar;

/// <summary>Invitation mail → the mailbox's copy of the meeting (the codec is faked: it is a port too).</summary>
public sealed class MessageEventUseCaseTests
{
    private const string Uid = "uid-1";
    private const string Ics = "BEGIN:VCALENDAR...";
    private static readonly MailboxRef Me = new("me@example.com");
    private static readonly MessageId Message = new("m1");
    private static readonly AttachmentId Part = new("2");
    private static readonly DateTimeOffset Original = new(2026, 6, 11, 23, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IMailMessageReader _mail = Substitute.For<IMailMessageReader>();
    private readonly IICalendarCodec _codec = Substitute.For<IICalendarCodec>();
    private readonly ICalendarReader _calendar = Substitute.For<ICalendarReader>();
    private readonly ISchedulingActions _scheduling = Substitute.For<ISchedulingActions>();
    private readonly IInvitationImporter _importer = Substitute.For<IInvitationImporter>();

    public MessageEventUseCaseTests() =>
        _importer.ImportAsync(Arg.Any<MailboxRef>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Error.NotFound("organizer inside"));

    private GetMessageEvent Resolve() => new(_mail, _codec, _calendar, _importer);

    private static CalendarEvent Meeting(string id, EventType type = EventType.SeriesMaster) =>
        new(new EventId(id), "Weekly", DateTimeTimeZone.Utc(Original), DateTimeTimeZone.Utc(Original.AddMinutes(30))) { ICalUId = Uid, Type = type };

    private void ArrangeInvitation(CalendarEvent invitation, string ics = Ics, string method = "REQUEST")
    {
        _mail.GetAsync(Me, Message, MessageFetch.Default, Ct).Returns(new MailMessage(Message) { CalendarPart = new CalendarPart(method, Part) });
        var bytes = Encoding.UTF8.GetBytes(ics);
        _mail.GetAttachmentContentAsync(Me, Message, Part, Ct).Returns(new BlobContent("text/calendar", bytes.Length, _ => Task.FromResult<Stream>(new MemoryStream(bytes))));
        _codec.Parse(ics).Returns(new CalendarDocument(method, [invitation]));
    }

    private void ArrangeCopies(params CalendarEvent[] copies) =>
        _calendar.ListAsync(Me, Arg.Is<ItemQuery>(q => IsUidFilter(q)), Ct).Returns(new Page<CalendarEvent>(copies));

    private static bool IsUidFilter(ItemQuery query) =>
        query.Filter is FilterNode.Compare { Property: "iCalUId", Op: CompareOp.Eq, Value: FilterValue.Text { Value: Uid } };

    [Fact]
    public async Task Resolves_the_series_by_the_invitations_uid()
    {
        ArrangeInvitation(Meeting(string.Empty, EventType.SingleInstance));
        ArrangeCopies(Meeting("master"));

        (await Resolve().ExecuteAsync(Me, Message, Ct)).Value.Id.ShouldBe(new EventId("master"));
    }

    [Fact]
    public async Task An_invitation_for_one_occurrence_resolves_to_that_occurrence()
    {
        ArrangeInvitation(Meeting(string.Empty, EventType.Exception) with { OriginalStart = Original });
        ArrangeCopies(Meeting("master"));
        _calendar.CalendarViewAsync(Me, Original.AddMinutes(-1), Original.AddMinutes(31), Arg.Any<ItemQuery>(), Ct)
            .Returns(new Page<CalendarEvent>(
            [
                Meeting("other") with { ICalUId = "someone-else", OriginalStart = Original, Type = EventType.Occurrence },
                Meeting("occurrence", EventType.Occurrence) with { OriginalStart = Original },
            ]));

        (await Resolve().ExecuteAsync(Me, Message, Ct)).Value.Id.ShouldBe(new EventId("occurrence"));
    }

    [Fact]
    public async Task An_external_counter_for_one_occurrence_resolves_to_the_organizers_occurrence()
    {
        // An Outlook attendee's COUNTER carries the proposed time as DTSTART/DTEND and the occurrence as
        // RECURRENCE-ID: the lookup must span both, then the organizer accepts it with a PATCH on that id.
        var proposed = Original.AddDays(2);
        ArrangeInvitation(new CalendarEvent(new EventId(string.Empty), "Weekly", DateTimeTimeZone.Utc(proposed), DateTimeTimeZone.Utc(proposed.AddMinutes(30)))
        {
            ICalUId = Uid,
            Type = EventType.Exception,
            OriginalStart = Original,
        }, method: "COUNTER");
        ArrangeCopies(Meeting("master") with { IsOrganizer = true });
        _calendar.CalendarViewAsync(Me, Original.AddMinutes(-1), proposed.AddMinutes(31), Arg.Any<ItemQuery>(), Ct)
            .Returns(new Page<CalendarEvent>([Meeting("occurrence", EventType.Occurrence) with { OriginalStart = Original, IsOrganizer = true }]));

        var resolved = (await Resolve().ExecuteAsync(Me, Message, Ct)).Value;

        resolved.Id.ShouldBe(new EventId("occurrence"));
        resolved.IsOrganizer.ShouldBeTrue();
    }

    [Fact]
    public async Task An_attendee_with_only_that_occurrence_gets_it_directly()
    {
        ArrangeInvitation(Meeting(string.Empty, EventType.Exception) with { OriginalStart = Original });
        ArrangeCopies(Meeting("exception", EventType.Exception) with { OriginalStart = Original });

        (await Resolve().ExecuteAsync(Me, Message, Ct)).Value.Id.ShouldBe(new EventId("exception"));
        await _calendar.DidNotReceiveWithAnyArgs().CalendarViewAsync(default, default, default, null!, Ct);
    }

    [Fact]
    public async Task A_missing_occurrence_is_not_found()
    {
        ArrangeInvitation(Meeting(string.Empty, EventType.Exception) with { OriginalStart = Original });
        ArrangeCopies(Meeting("master"));
        _calendar.CalendarViewAsync(Me, Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<ItemQuery>(), Ct).Returns(Page<CalendarEvent>.Empty);

        (await Resolve().ExecuteAsync(Me, Message, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Everything_unresolvable_is_not_found_so_the_client_falls_back()
    {
        // Not a meeting message.
        _mail.GetAsync(Me, Message, MessageFetch.Default, Ct).Returns(new MailMessage(Message));
        (await Resolve().ExecuteAsync(Me, Message, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);

        // Unparseable calendar part.
        ArrangeInvitation(Meeting(string.Empty));
        _codec.Parse(Ics).Returns(Error.Validation("bad"));
        (await Resolve().ExecuteAsync(Me, Message, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);

        // No UID.
        ArrangeInvitation(Meeting(string.Empty) with { ICalUId = null });
        (await Resolve().ExecuteAsync(Me, Message, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);

        // Not in the calendar.
        ArrangeInvitation(Meeting(string.Empty));
        ArrangeCopies();
        (await Resolve().ExecuteAsync(Me, Message, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task An_invitation_missing_from_the_calendar_is_imported_then_resolved()
    {
        ArrangeInvitation(Meeting(string.Empty, EventType.SingleInstance));
        _calendar.ListAsync(Me, Arg.Is<ItemQuery>(q => IsUidFilter(q)), Ct)
            .Returns(new Page<CalendarEvent>([]), new Page<CalendarEvent>([Meeting("imported", EventType.SingleInstance)]));
        _importer.ImportAsync(Me, Ics, Ct).Returns(Result.Success());

        (await Resolve().ExecuteAsync(Me, Message, Ct)).Value.Id.ShouldBe(new EventId("imported"));
        await _importer.Received(1).ImportAsync(Me, Ics, Ct);
    }

    [Fact]
    public async Task Only_an_invitation_is_imported_and_an_import_failure_passes_through()
    {
        ArrangeInvitation(Meeting(string.Empty), method: "CANCEL");
        ArrangeCopies();
        (await Resolve().ExecuteAsync(Me, Message, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        await _importer.DidNotReceiveWithAnyArgs().ImportAsync(default!, default!, Ct);

        ArrangeInvitation(Meeting(string.Empty));
        _importer.ImportAsync(Me, Ics, Ct).Returns(Error.Forbidden("no calendar"));
        (await Resolve().ExecuteAsync(Me, Message, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task An_oversized_calendar_part_is_not_read()
    {
        ArrangeInvitation(Meeting(string.Empty), new string('x', GetMessageEvent.MaxCalendarPartBytes + 1));
        (await Resolve().ExecuteAsync(Me, Message, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _codec.DidNotReceiveWithAnyArgs().Parse(null!);
    }

    [Fact]
    public async Task Mail_and_calendar_failures_pass_through()
    {
        _mail.GetAsync(Me, Message, MessageFetch.Default, Ct).Returns(Error.Forbidden("no"));
        (await Resolve().ExecuteAsync(Me, Message, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);

        ArrangeInvitation(Meeting(string.Empty));
        _calendar.ListAsync(Me, Arg.Any<ItemQuery>(), Ct).Returns(Error.BackendUnavailable("down"));
        (await Resolve().ExecuteAsync(Me, Message, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task Responding_from_the_mail_answers_the_resolved_meeting()
    {
        ArrangeInvitation(Meeting(string.Empty));
        ArrangeCopies(Meeting("master"));
        _scheduling.RespondAsync(Me, new EventId("master"), RsvpResponse.TentativelyAccept, null, true, null, Ct).Returns(Result<ItipMessage?>.Success(null));
        var respond = new RespondToMessage(Resolve(), new RespondToEvent(_scheduling, Substitute.For<IItipMail>()));

        (await respond.ExecuteAsync(Me, Message, new RsvpRequest(RsvpResponse.TentativelyAccept, string.Empty, true), Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task The_organizer_gets_a_400_and_nothing_is_sent()
    {
        ArrangeInvitation(Meeting(string.Empty));
        ArrangeCopies(Meeting("master") with { IsOrganizer = true });
        var respond = new RespondToMessage(Resolve(), new RespondToEvent(_scheduling, Substitute.For<IItipMail>()));

        (await respond.ExecuteAsync(Me, Message, new RsvpRequest(RsvpResponse.Accept, null, true), Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        await _scheduling.DidNotReceiveWithAnyArgs().RespondAsync(default, default, default, default, default, default, Ct);
    }
}

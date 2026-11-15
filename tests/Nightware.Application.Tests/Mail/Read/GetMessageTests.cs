using System.Text;
using Nightware.Application.Calendar;
using Nightware.Application.Common.Query;
using Nightware.Application.Mail;
using Nightware.Application.Mail.Read;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Tests.Mail.Read;

public sealed class GetMessageTests
{
    private const string Ics = "BEGIN:VCALENDAR\r\nMETHOD:REQUEST\r\nEND:VCALENDAR\r\n";

    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;
    private static readonly MailboxRef Mailbox = new("me@example.com");
    private static readonly MessageId Id = new("e.m1");
    private static readonly AttachmentId Part = new("4");

    private readonly IMailMessageReader _messages = Substitute.For<IMailMessageReader>();
    private readonly IICalendarCodec _codec = Substitute.For<IICalendarCodec>();

    private GetMessage UseCase => new(_messages, _codec);

    private static MailMessage Invitation(string method = "REQUEST") => new(Id)
    {
        Subject = "Quarterly review",
        From = new EmailAddress("alex@example.com"),
        CalendarPart = new CalendarPart(method, Part),
    };

    private static CalendarEvent Event(params Attendee[] attendees) =>
        new(new EventId(string.Empty), "Quarterly review", new DateTimeTimeZone(new DateTime(2026, 6, 10, 9, 0, 0), "UTC"), new DateTimeTimeZone(new DateTime(2026, 6, 10, 10, 0, 0), "UTC"))
        {
            Location = new Location("Boardroom"),
            Attendees = attendees,
        };

    private static BlobContent Blob(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return new BlobContent("text/calendar", bytes.Length, _ => Task.FromResult<Stream>(new MemoryStream(bytes)));
    }

    private void Arrange(MailMessage message, CalendarDocument? document = null)
    {
        _messages.GetAsync(Mailbox, Id, Arg.Any<MessageFetch>(), Ct).Returns(message);
        _messages.GetAttachmentContentAsync(Mailbox, Id, Part, Ct).Returns(Blob(Ics));
        if (document is not null)
        {
            _codec.Parse(Ics).Returns(document);
        }
    }

    [Fact]
    public async Task An_ordinary_message_is_returned_as_is()
    {
        var message = new MailMessage(Id) { Subject = "Hi" };
        Arrange(message);

        var view = (await UseCase.ExecuteAsync(Mailbox, Id, MessageFetch.Default, Ct)).Value;

        view.ShouldBe(new MessageView(message));
        await _messages.DidNotReceiveWithAnyArgs().GetAttachmentContentAsync(default, default, default, Ct);
    }

    [Fact]
    public async Task An_invitation_gets_its_meeting_time_place_and_type()
    {
        Arrange(Invitation(), new CalendarDocument("REQUEST", [Event()]));

        var view = (await UseCase.ExecuteAsync(Mailbox, Id, MessageFetch.Default, Ct)).Value;

        view.Meeting.ShouldBe(new MeetingDetails
        {
            Start = new DateTimeOffset(2026, 6, 10, 9, 0, 0, TimeSpan.Zero),
            End = new DateTimeOffset(2026, 6, 10, 10, 0, 0, TimeSpan.Zero),
            Location = "Boardroom",
            MessageType = MeetingMessageType.MeetingRequest,
        });
    }

    [Fact]
    public async Task A_reply_takes_its_type_from_the_senders_partstat()
    {
        var bob = new Attendee(new EmailAddress("bob@example.com"), AttendeeType.Required, new ResponseStatus(ResponseType.Accepted));
        var alex = new Attendee(new EmailAddress("ALEX@example.com"), AttendeeType.Required, new ResponseStatus(ResponseType.Declined));
        Arrange(Invitation("REPLY"), new CalendarDocument("REPLY", [Event(bob, alex)]));

        var view = (await UseCase.ExecuteAsync(Mailbox, Id, MessageFetch.Default, Ct)).Value;

        view.Message.CalendarPart!.ReplyPartStat.ShouldBe("DECLINED");
        view.Meeting!.MessageType.ShouldBe(MeetingMessageType.MeetingDeclined);
    }

    [Theory]
    [InlineData(ResponseType.TentativelyAccepted, "TENTATIVE", MeetingMessageType.MeetingTentativelyAccepted)]
    [InlineData(ResponseType.Accepted, "ACCEPTED", MeetingMessageType.MeetingAccepted)]
    [InlineData(ResponseType.NotResponded, "NEEDS-ACTION", MeetingMessageType.None)]
    [InlineData(ResponseType.None, null, MeetingMessageType.None)]
    public async Task A_reply_from_a_lone_attendee_uses_that_attendee(ResponseType response, string? partStat, MeetingMessageType expected)
    {
        var someone = new Attendee(new EmailAddress("someone@example.com"), AttendeeType.Required, new ResponseStatus(response));
        Arrange(Invitation("REPLY"), new CalendarDocument("REPLY", [Event(someone)]));

        var view = (await UseCase.ExecuteAsync(Mailbox, Id, MessageFetch.Default, Ct)).Value;

        view.Message.CalendarPart!.ReplyPartStat.ShouldBe(partStat);
        view.Meeting!.MessageType.ShouldBe(expected);
    }

    [Fact]
    public async Task A_counter_proposal_is_a_decline_carrying_the_proposed_time()
    {
        Arrange(Invitation("COUNTER"), new CalendarDocument("COUNTER", [Event()]));

        var meeting = (await UseCase.ExecuteAsync(Mailbox, Id, MessageFetch.Default, Ct)).Value.Meeting!;

        meeting.MessageType.ShouldBe(MeetingMessageType.MeetingDeclined);
        meeting.ProposedStart.ShouldBe(new DateTimeOffset(2026, 6, 10, 9, 0, 0, TimeSpan.Zero));
        meeting.ProposedEnd.ShouldBe(new DateTimeOffset(2026, 6, 10, 10, 0, 0, TimeSpan.Zero));
        meeting.Start.ShouldBeNull();
    }

    [Fact]
    public async Task Zoned_and_all_day_times_become_instants()
    {
        var sydney = Event() with
        {
            Start = new DateTimeTimeZone(new DateTime(2026, 6, 10, 9, 0, 0), "AUS Eastern Standard Time"),
            End = new DateTimeTimeZone(new DateTime(2026, 6, 11, 0, 0, 0), "UTC"),
            IsAllDay = true,
            Location = new Location(" "),
        };
        Arrange(Invitation(), new CalendarDocument("REQUEST", [sydney]));

        var meeting = (await UseCase.ExecuteAsync(Mailbox, Id, MessageFetch.Default, Ct)).Value.Meeting!;

        meeting.Start.ShouldBe(new DateTimeOffset(2026, 6, 9, 23, 0, 0, TimeSpan.Zero));
        meeting.IsAllDay.ShouldBeTrue();
        meeting.Location.ShouldBeNull();
    }

    [Fact]
    public async Task The_series_master_describes_the_meeting_not_its_exceptions()
    {
        var exception = Event() with { Subject = "moved", OriginalStart = DateTimeOffset.UnixEpoch, Location = new Location("Elsewhere") };
        Arrange(Invitation(), new CalendarDocument("REQUEST", [exception, Event()]));

        (await UseCase.ExecuteAsync(Mailbox, Id, MessageFetch.Default, Ct)).Value.Meeting!.Location.ShouldBe("Boardroom");
    }

    [Fact]
    public async Task With_select_the_calendar_part_is_not_read()
    {
        Arrange(Invitation());

        var view = (await UseCase.ExecuteAsync(Mailbox, Id, new MessageFetch(new HashSet<string> { "internetMessageHeaders", "body" }, BodyType.Text), Ct)).Value;

        view.Meeting.ShouldBeNull();
        await _messages.DidNotReceiveWithAnyArgs().GetAttachmentContentAsync(default, default, default, Ct);
    }

    [Fact]
    public async Task An_unreadable_or_unparsable_part_leaves_the_message_plain()
    {
        _messages.GetAsync(Mailbox, Id, Arg.Any<MessageFetch>(), Ct).Returns(Invitation());
        _messages.GetAttachmentContentAsync(Mailbox, Id, Part, Ct).Returns(Error.NotFound("gone"));

        (await UseCase.ExecuteAsync(Mailbox, Id, MessageFetch.Default, Ct)).Value.Meeting.ShouldBeNull();

        _messages.GetAttachmentContentAsync(Mailbox, Id, Part, Ct).Returns(Blob(Ics));
        _codec.Parse(Ics).Returns(Error.Validation("bad"));
        (await UseCase.ExecuteAsync(Mailbox, Id, MessageFetch.Default, Ct)).Value.Meeting.ShouldBeNull();

        _codec.Parse(Ics).Returns(new CalendarDocument("REQUEST", []));
        (await UseCase.ExecuteAsync(Mailbox, Id, MessageFetch.Default, Ct)).Value.Meeting.ShouldBeNull();
    }

    [Fact]
    public async Task A_stream_that_fails_or_is_too_big_leaves_the_message_plain()
    {
        _messages.GetAsync(Mailbox, Id, Arg.Any<MessageFetch>(), Ct).Returns(Invitation());
        _messages.GetAttachmentContentAsync(Mailbox, Id, Part, Ct)
            .Returns(new BlobContent("text/calendar", null, _ => throw new IOException("reset")));

        (await UseCase.ExecuteAsync(Mailbox, Id, MessageFetch.Default, Ct)).Value.Meeting.ShouldBeNull();

        var huge = new byte[GetMessage.MaxCalendarPartBytes + 1];
        _messages.GetAttachmentContentAsync(Mailbox, Id, Part, Ct)
            .Returns(new BlobContent("text/calendar", null, _ => Task.FromResult<Stream>(new MemoryStream(huge))));
        (await UseCase.ExecuteAsync(Mailbox, Id, MessageFetch.Default, Ct)).Value.Meeting.ShouldBeNull();

        _messages.GetAttachmentContentAsync(Mailbox, Id, Part, Ct).Returns(new BlobContent("text/calendar", huge.Length, _ => throw new InvalidOperationException("not opened")));
        (await UseCase.ExecuteAsync(Mailbox, Id, MessageFetch.Default, Ct)).Value.Meeting.ShouldBeNull();
        _codec.DidNotReceiveWithAnyArgs().Parse(default!);
    }

    [Fact]
    public async Task Failures_and_unsupported_expansions_are_errors()
    {
        _messages.GetAsync(Mailbox, Id, Arg.Any<MessageFetch>(), Ct).Returns(Error.NotFound("gone"));

        (await UseCase.ExecuteAsync(Mailbox, Id, MessageFetch.Default, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await UseCase.ExecuteAsync(Mailbox, Id, new MessageFetch(ExpandAttachments: new ExpandClause("extensions")), Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }
}

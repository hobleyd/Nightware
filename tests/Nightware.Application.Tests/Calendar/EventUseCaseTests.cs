using Nightware.Application.Calendar;
using Nightware.Application.Calendar.Events;
using Nightware.Application.Calendar.OnlineMeetings;
using Nightware.Application.Common.Query;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Calendar;

public sealed class EventUseCaseTests
{
    private static readonly MailboxRef Me = new("me@example.com");
    private static readonly EventId Id = new("e1");
    private static readonly DateTimeOffset Start = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly ICalendarReader _reader = Substitute.For<ICalendarReader>();
    private readonly ICalendarWriter _writer = Substitute.For<ICalendarWriter>();
    private readonly IOnlineMeetings _meetings = Substitute.For<IOnlineMeetings>();

    private OnlineMeetingRooms Rooms => new(_meetings, _reader);

    private static DateTimeTimeZone Sydney(int hour) => new(new DateTime(2026, 6, 10, hour, 0, 0), "Australia/Sydney");

    private static CalendarEvent Event() => new(Id, "Review", Sydney(9), Sydney(10));

    [Fact]
    public async Task Calendar_view_needs_an_ordered_window()
    {
        var view = new GetCalendarView(_reader);
        (await view.ExecuteAsync(Me, Start, Start, ItemQuery.Default, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        await _reader.DidNotReceiveWithAnyArgs().CalendarViewAsync(default, default, default, null!, Ct);

        _reader.CalendarViewAsync(Me, Start, Start.AddDays(1), ItemQuery.Default, Ct).Returns(new Page<CalendarEvent>([Event()]));
        (await view.ExecuteAsync(Me, Start, Start.AddDays(1), ItemQuery.Default, Ct)).Value.Items.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Calendar_view_serves_a_window_up_to_the_cap()
    {
        var end = Start + CalendarViewLimits.MaxWindow;
        _reader.CalendarViewAsync(Me, Start, end, ItemQuery.Default, Ct).Returns(new Page<CalendarEvent>([Event()]));

        (await new GetCalendarView(_reader).ExecuteAsync(Me, Start, end, ItemQuery.Default, Ct)).Value.Items.ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(365_000L * TimeSpan.TicksPerDay)]
    public async Task Calendar_view_refuses_a_window_over_the_cap_without_expanding_it(long extraTicks)
    {
        var view = new GetCalendarView(_reader);
        var end = Start + CalendarViewLimits.MaxWindow + TimeSpan.FromTicks(extraTicks);

        var result = await view.ExecuteAsync(Me, Start, end, ItemQuery.Default, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        result.Error.Target.ShouldBe("endDateTime");
        await _reader.DidNotReceiveWithAnyArgs().CalendarViewAsync(default, default, default, null!, Ct);
    }

    [Fact]
    public void Calendar_view_cap_covers_the_clients_longest_window()
    {
        // NightMail's out-of-office meeting sweep: pickers span a year back to five years forward.
        var sweep = new DateTimeOffset(2031, 1, 1, 0, 0, 0, TimeSpan.Zero) - new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        CalendarViewLimits.MaxWindow.ShouldBeGreaterThan(sweep);
    }

    [Fact]
    public async Task Reads_pass_through()
    {
        _reader.GetAsync(Me, Id, Ct).Returns(Event());
        _reader.ListAsync(Me, ItemQuery.Default, Ct).Returns(Error.Validation("filter"));

        (await new GetEvent(_reader).ExecuteAsync(Me, Id, Ct)).Value.Subject.ShouldBe("Review");
        (await new ListEvents(_reader).ExecuteAsync(Me, ItemQuery.Default, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public async Task Create_writes_a_valid_event()
    {
        var newEvent = new NewEvent("Review", Sydney(9), Sydney(10)) { Attendees = [new Attendee(new EmailAddress("bob@example.com"), AttendeeType.Required, new ResponseStatus(ResponseType.None))] };
        _writer.CreateAsync(Me, newEvent, Ct).Returns(Event());

        (await new CreateEvent(_writer, Rooms).ExecuteAsync(Me, newEvent, Ct)).Value.Id.ShouldBe(Id);
    }

    public static TheoryData<NewEvent> InvalidNewEvents() => new()
    {
        new NewEvent("end before start", Sydney(10), Sydney(9)),
        new NewEvent("unknown zone", new DateTimeTimeZone(new DateTime(2026, 6, 10, 9, 0, 0), "Mars/Olympus"), Sydney(10)),
        new NewEvent("bad attendee", Sydney(9), Sydney(10)) { Attendees = [new Attendee(new EmailAddress("not-an-address"), AttendeeType.Required, new ResponseStatus(ResponseType.None))] },
        new NewEvent("negative reminder", Sydney(9), Sydney(10)) { ReminderMinutesBeforeStart = -5 },
    };

    [Theory]
    [MemberData(nameof(InvalidNewEvents))]
    public async Task Create_rejects_invalid_events_without_writing(NewEvent newEvent)
    {
        (await new CreateEvent(_writer, Rooms).ExecuteAsync(Me, newEvent, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        await _writer.DidNotReceiveWithAnyArgs().CreateAsync(default, null!, Ct);
    }

    [Fact]
    public async Task Create_allows_a_zero_length_event_and_compares_instants_across_zones()
    {
        // 09:00 Sydney is 23:00 UTC the day before: an end in UTC after it is fine.
        var newEvent = new NewEvent("x", Sydney(9), DateTimeTimeZone.Utc(new DateTimeOffset(2026, 6, 9, 23, 30, 0, TimeSpan.Zero)));
        _writer.CreateAsync(Me, newEvent, Ct).Returns(Event());
        (await new CreateEvent(_writer, Rooms).ExecuteAsync(Me, newEvent, Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Update_validates_only_what_the_patch_carries()
    {
        var update = new UpdateEvent(_writer, Rooms);
        _writer.UpdateAsync(Me, Id, Arg.Any<EventPatch>(), Ct).Returns(Event());

        (await update.ExecuteAsync(Me, Id, new EventPatch { Start = Sydney(9) }, Ct)).IsSuccess.ShouldBeTrue();
        (await update.ExecuteAsync(Me, Id, new EventPatch { Start = Sydney(10), End = Sydney(9) }, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await update.ExecuteAsync(Me, Id, new EventPatch { End = new DateTimeTimeZone(DateTime.UnixEpoch, "Nowhere/Land") }, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await update.ExecuteAsync(Me, Id, new EventPatch { ReminderMinutesBeforeStart = -1 }, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await update.ExecuteAsync(Me, Id, new EventPatch { Attendees = [new Attendee(new EmailAddress("a@b@c"), AttendeeType.Required, new ResponseStatus(ResponseType.None))] }, Ct))
            .Error!.Kind.ShouldBe(ErrorKind.Validation);
        await _writer.Received(1).UpdateAsync(Me, Id, Arg.Any<EventPatch>(), Ct);
    }

    [Fact]
    public async Task Delete_passes_through()
    {
        _writer.DeleteAsync(Me, Id, Ct).Returns(Result.Success());
        (await new DeleteEvent(_writer, Rooms).ExecuteAsync(Me, Id, Ct)).IsSuccess.ShouldBeTrue();
    }
}

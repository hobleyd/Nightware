using Nightware.Application.Calendar;
using Nightware.Application.Calendar.Calendars;
using Nightware.Application.Calendar.Events;
using Nightware.Application.Calendar.OnlineMeetings;
using Nightware.Application.Calendar.Scheduling;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Calendar.OnlineMeetings;

/// <summary>An event's meeting room through the event use cases that open, keep and close it.</summary>
public sealed class OnlineMeetingRoomsTests
{
    private const string Room = "https://cloud.example.com/cloud/call/pv8f59mw";
    private static readonly MailboxRef Me = new("me@example.com");
    private static readonly EventId Id = new("e1");

    // 09:00–10:00 Sydney on 10 June is 23:00–00:00 UTC the day before.
    private static readonly DateTimeOffset NineSydney = new(2026, 6, 9, 23, 0, 0, TimeSpan.Zero);

    private readonly ICalendarReader _reader = Substitute.For<ICalendarReader>();
    private readonly ICalendarWriter _writer = Substitute.For<ICalendarWriter>();
    private readonly IOnlineMeetings _meetings = Substitute.For<IOnlineMeetings>();

    public OnlineMeetingRoomsTests()
    {
        _meetings.IsEnabled.Returns(true);
        _meetings.OpenAsync(Arg.Any<OnlineMeetingRequest>(), Arg.Any<CancellationToken>()).Returns(Result<string>.Success(Room));
        _meetings.KeepAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _meetings.CloseAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _writer.CreateAsync(Me, Arg.Any<NewEvent>(), Arg.Any<CancellationToken>()).Returns(Stored());
        _writer.UpdateAsync(Me, Id, Arg.Any<EventPatch>(), Arg.Any<CancellationToken>()).Returns(Stored());
        _writer.DeleteAsync(Me, Id, Arg.Any<CancellationToken>()).Returns(Result.Success());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private OnlineMeetingRooms Rooms => new(_meetings, _reader);

    private static DateTimeTimeZone Sydney(int hour) => new(new DateTime(2026, 6, 10, hour, 0, 0), "Australia/Sydney");

    private static NewEvent Online() => new("Review", Sydney(9), Sydney(10)) { IsOnlineMeeting = true };

    private static CalendarEvent Stored(string? url = null, bool organizer = true) =>
        new(Id, "Review", Sydney(9), Sydney(10)) { IsOrganizer = organizer, OnlineMeetingUrl = url, IsOnlineMeeting = url is not null };

    private void Current(CalendarEvent stored) => _reader.GetAsync(Me, Id, Arg.Any<CancellationToken>()).Returns(stored);

    [Fact]
    public async Task An_online_event_is_written_with_a_room_bound_to_its_span()
    {
        (await new CreateEvent(_writer, Rooms).ExecuteAsync(Me, Online(), Ct)).IsSuccess.ShouldBeTrue();

        await _meetings.Received(1).OpenAsync(new OnlineMeetingRequest("Review", NineSydney, NineSydney.AddHours(1)), Ct);
        await _writer.Received(1).CreateAsync(Me, Arg.Is<NewEvent>(e => e.OnlineMeetingUrl == Room), Ct);
    }

    [Fact]
    public async Task A_series_room_has_no_end()
    {
        var series = Online() with
        {
            Recurrence = new PatternedRecurrence(new RecurrencePattern(RecurrencePatternType.Daily, 1), new RecurrenceRange(RecurrenceRangeType.NoEnd, new DateOnly(2026, 6, 10))),
        };

        await new CreateEvent(_writer, Rooms).ExecuteAsync(Me, series, Ct);

        await _meetings.Received(1).OpenAsync(new OnlineMeetingRequest("Review", NineSydney, null), Ct);
    }

    [Fact]
    public async Task A_room_that_cannot_be_opened_leaves_the_event_without_one()
    {
        _meetings.OpenAsync(Arg.Any<OnlineMeetingRequest>(), Ct).Returns(Error.BackendUnavailable("down"));

        (await new CreateEvent(_writer, Rooms).ExecuteAsync(Me, Online(), Ct)).IsSuccess.ShouldBeTrue();

        await _writer.Received(1).CreateAsync(Me, Arg.Is<NewEvent>(e => e.OnlineMeetingUrl == null && e.IsOnlineMeeting), Ct);
    }

    [Fact]
    public async Task A_failed_write_closes_the_room_opened_for_it()
    {
        _writer.CreateAsync(Me, Arg.Any<NewEvent>(), Ct).Returns(Error.Conflict("exists"));

        (await new CreateEvent(_writer, Rooms).ExecuteAsync(Me, Online(), Ct)).Error!.Kind.ShouldBe(ErrorKind.Conflict);

        await _meetings.Received(1).CloseAsync(Room, Ct);
    }

    [Fact]
    public async Task A_failed_write_keeps_a_room_the_caller_brought()
    {
        _writer.CreateAsync(Me, Arg.Any<NewEvent>(), Ct).Returns(Error.Conflict("exists"));

        await new CreateEvent(_writer, Rooms).ExecuteAsync(Me, Online() with { OnlineMeetingUrl = Room }, Ct);

        await _meetings.DidNotReceiveWithAnyArgs().OpenAsync(null!, Ct);
        await _meetings.DidNotReceiveWithAnyArgs().CloseAsync(null!, Ct);
    }

    [Fact]
    public async Task Without_a_provider_nothing_is_opened_or_read()
    {
        _meetings.IsEnabled.Returns(false);

        await new CreateEvent(_writer, Rooms).ExecuteAsync(Me, Online(), Ct);
        await new UpdateEvent(_writer, Rooms).ExecuteAsync(Me, Id, new EventPatch { IsOnlineMeeting = true, Start = Sydney(11), End = Sydney(12) }, Ct);
        await new DeleteEvent(_writer, Rooms).ExecuteAsync(Me, Id, Ct);

        await _meetings.DidNotReceiveWithAnyArgs().OpenAsync(null!, Ct);
        await _reader.DidNotReceiveWithAnyArgs().GetAsync(default, default, Ct);
    }

    [Fact]
    public async Task An_event_not_asking_to_be_online_gets_no_room()
    {
        await new CreateEvent(_writer, Rooms).ExecuteAsync(Me, Online() with { IsOnlineMeeting = false }, Ct);

        await _meetings.DidNotReceiveWithAnyArgs().OpenAsync(null!, Ct);
    }

    [Fact]
    public async Task A_calendars_new_online_event_gets_a_room_too()
    {
        var events = Substitute.For<ICalendarEventsIn>();
        var calendar = new CalendarId("c2");
        events.CreateAsync(Me, calendar, Arg.Any<NewEvent>(), Ct).Returns(Error.Forbidden("read-only"), Stored());
        var use = new CalendarEventsIn(events, Rooms);

        (await use.CreateAsync(Me, calendar, Online(), Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        await _meetings.Received(1).CloseAsync(Room, Ct);

        (await use.CreateAsync(Me, calendar, Online(), Ct)).IsSuccess.ShouldBeTrue();
        await events.Received(2).CreateAsync(Me, calendar, Arg.Is<NewEvent>(e => e.OnlineMeetingUrl == Room), Ct);
    }

    [Fact]
    public async Task Turning_an_organizers_event_online_opens_a_room_for_its_new_times()
    {
        Current(Stored());

        await new UpdateEvent(_writer, Rooms).ExecuteAsync(Me, Id, new EventPatch { IsOnlineMeeting = true, Start = Sydney(11), End = Sydney(12) }, Ct);

        await _meetings.Received(1).OpenAsync(new OnlineMeetingRequest("Review", NineSydney.AddHours(2), NineSydney.AddHours(3)), Ct);
        await _writer.Received(1).UpdateAsync(Me, Id, Arg.Is<EventPatch>(p => p.OnlineMeetingUrl == Room), Ct);
    }

    [Fact]
    public async Task A_series_or_its_occurrence_turned_online_gets_a_room_without_an_end()
    {
        var daily = new PatternedRecurrence(new RecurrencePattern(RecurrencePatternType.Daily, 1), new RecurrenceRange(RecurrenceRangeType.NoEnd, new DateOnly(2026, 6, 10)));
        Current(Stored() with { Recurrence = daily });
        await new UpdateEvent(_writer, Rooms).ExecuteAsync(Me, Id, new EventPatch { IsOnlineMeeting = true }, Ct);

        Current(Stored() with { SeriesMasterId = new EventId("s") });
        await new UpdateEvent(_writer, Rooms).ExecuteAsync(Me, Id, new EventPatch { IsOnlineMeeting = true }, Ct);

        await _meetings.Received(2).OpenAsync(new OnlineMeetingRequest("Review", NineSydney, null), Ct);
    }

    [Fact]
    public async Task A_series_made_single_gets_a_room_with_an_end()
    {
        var daily = new PatternedRecurrence(new RecurrencePattern(RecurrencePatternType.Daily, 1), new RecurrenceRange(RecurrenceRangeType.NoEnd, new DateOnly(2026, 6, 10)));
        Current(Stored() with { Recurrence = daily });

        await new UpdateEvent(_writer, Rooms).ExecuteAsync(Me, Id, new EventPatch { IsOnlineMeeting = true, ClearRecurrence = true }, Ct);

        await _meetings.Received(1).OpenAsync(new OnlineMeetingRequest("Review", NineSydney, NineSydney.AddHours(1)), Ct);
    }

    [Fact]
    public async Task An_attendees_copy_or_an_event_that_has_a_room_gets_none()
    {
        Current(Stored(organizer: false));
        await new UpdateEvent(_writer, Rooms).ExecuteAsync(Me, Id, new EventPatch { IsOnlineMeeting = true }, Ct);

        Current(Stored(Room));
        await new UpdateEvent(_writer, Rooms).ExecuteAsync(Me, Id, new EventPatch { IsOnlineMeeting = true }, Ct);

        await _meetings.DidNotReceiveWithAnyArgs().OpenAsync(null!, Ct);
        await _writer.Received(2).UpdateAsync(Me, Id, Arg.Is<EventPatch>(p => p.OnlineMeetingUrl == null), Ct);
    }

    [Fact]
    public async Task A_patch_that_neither_asks_for_a_room_nor_makes_a_series_reads_nothing()
    {
        await new UpdateEvent(_writer, Rooms).ExecuteAsync(Me, Id, new EventPatch { Subject = "Renamed", IsOnlineMeeting = false }, Ct);
        await new UpdateEvent(_writer, Rooms).ExecuteAsync(Me, Id, new EventPatch { Start = Sydney(14), End = Sydney(15) }, Ct);

        await _reader.DidNotReceiveWithAnyArgs().GetAsync(default, default, Ct);
    }

    [Fact]
    public async Task An_event_that_cannot_be_read_is_written_as_asked()
    {
        _reader.GetAsync(Me, Id, Ct).Returns(Error.NotFound("gone"));
        _writer.UpdateAsync(Me, Id, Arg.Any<EventPatch>(), Ct).Returns(Error.NotFound("gone"));

        (await new UpdateEvent(_writer, Rooms).ExecuteAsync(Me, Id, new EventPatch { IsOnlineMeeting = true }, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);

        await _meetings.DidNotReceiveWithAnyArgs().OpenAsync(null!, Ct);
        await _meetings.DidNotReceiveWithAnyArgs().CloseAsync(null!, Ct);
    }

    [Fact]
    public async Task A_failed_update_closes_the_room_opened_for_it()
    {
        Current(Stored());
        _writer.UpdateAsync(Me, Id, Arg.Any<EventPatch>(), Ct).Returns(Error.Conflict("changed"));

        await new UpdateEvent(_writer, Rooms).ExecuteAsync(Me, Id, new EventPatch { IsOnlineMeeting = true }, Ct);

        await _meetings.Received(1).CloseAsync(Room, Ct);
    }

    [Fact]
    public async Task A_moved_or_renamed_meeting_leaves_its_room_to_follow_the_event_and_a_series_keeps_it()
    {
        Current(Stored(Room));
        var update = new UpdateEvent(_writer, Rooms);

        // Talk re-times and renames the room from the stored event itself (ADR 0009 D7): nothing to do here.
        await update.ExecuteAsync(Me, Id, new EventPatch { Start = Sydney(9), End = Sydney(10) }, Ct);
        await update.ExecuteAsync(Me, Id, new EventPatch { Start = Sydney(8), End = Sydney(9) }, Ct);
        await update.ExecuteAsync(Me, Id, new EventPatch { Start = Sydney(14), End = Sydney(15), Subject = "Moved" }, Ct);
        await _meetings.DidNotReceiveWithAnyArgs().KeepAsync(null!, Ct);

        await update.ExecuteAsync(Me, Id, new EventPatch
        {
            Recurrence = new PatternedRecurrence(new RecurrencePattern(RecurrencePatternType.Weekly, 1) { DaysOfWeek = [DayOfWeek.Wednesday] }, new RecurrenceRange(RecurrenceRangeType.NoEnd, new DateOnly(2026, 6, 10))),
        }, Ct);
        await _meetings.Received(1).KeepAsync(Room, Ct);
    }

    [Fact]
    public async Task Deleting_or_cancelling_the_organizers_meeting_closes_its_room()
    {
        Current(Stored(Room));
        var scheduling = Substitute.For<ISchedulingActions>();
        scheduling.CancelAsync(Me, Id, "off", Ct).Returns(Result.Success());

        (await new DeleteEvent(_writer, Rooms).ExecuteAsync(Me, Id, Ct)).IsSuccess.ShouldBeTrue();
        (await new CancelEvent(scheduling, Rooms).ExecuteAsync(Me, Id, "off", Ct)).IsSuccess.ShouldBeTrue();

        await _meetings.Received(2).CloseAsync(Room, Ct);
    }

    [Fact]
    public async Task An_occurrence_an_attendees_copy_or_a_failed_delete_keeps_the_room()
    {
        var delete = new DeleteEvent(_writer, Rooms);

        Current(Stored(Room) with { SeriesMasterId = new EventId("s") });
        await delete.ExecuteAsync(Me, Id, Ct);

        Current(Stored(Room, organizer: false));
        await delete.ExecuteAsync(Me, Id, Ct);

        Current(Stored(Room));
        _writer.DeleteAsync(Me, Id, Ct).Returns(Error.Forbidden("no"));
        (await delete.ExecuteAsync(Me, Id, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);

        var scheduling = Substitute.For<ISchedulingActions>();
        scheduling.CancelAsync(Me, Id, null, Ct).Returns(Error.Forbidden("not the organizer"));
        (await new CancelEvent(scheduling, Rooms).ExecuteAsync(Me, Id, null, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);

        await _meetings.DidNotReceiveWithAnyArgs().CloseAsync(null!, Ct);
    }

    [Fact]
    public async Task An_event_without_a_room_closes_nothing()
    {
        Current(Stored());

        await new DeleteEvent(_writer, Rooms).ExecuteAsync(Me, Id, Ct);

        await _meetings.DidNotReceiveWithAnyArgs().CloseAsync(null!, Ct);
    }
}

using Nightware.Application.Calendar.Calendars;
using Nightware.Application.Common.Query;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Calendar.Calendars;

public sealed class CalendarCollectionUseCaseTests
{
    private static readonly MailboxRef Me = new("me@example.com");
    private static readonly EventCalendar Personal = new(new CalendarId("c1"), "Personal") { IsDefault = true };
    private static readonly EventCalendar Work = new(new CalendarId("c2"), "Work") { IsRemovable = true };
    private readonly ICalendarStore _store = Substitute.For<ICalendarStore>();

    private static Nightware.Application.Calendar.OnlineMeetings.OnlineMeetingRooms NoRooms =>
        new(Substitute.For<Nightware.Application.Calendar.OnlineMeetings.IOnlineMeetings>(), Substitute.For<Nightware.Application.Calendar.ICalendarReader>());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public CalendarCollectionUseCaseTests() =>
        _store.ListAsync(Me, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<EventCalendar>>.Success([Personal, Work]));

    [Fact]
    public async Task Get_finds_by_id_or_the_default()
    {
        (await new GetCalendar(_store).ExecuteAsync(Me, null, Ct)).Value.ShouldBe(Personal);
        (await new GetCalendar(_store).ExecuteAsync(Me, Work.Id, Ct)).Value.ShouldBe(Work);
        (await new GetCalendar(_store).ExecuteAsync(Me, new CalendarId("x"), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Create_maps_a_graph_colour_and_needs_a_name()
    {
        _store.CreateAsync(Me, "Projects", "#87D28E", Ct).Returns(Work);

        (await new CreateCalendar(_store).ExecuteAsync(Me, " Projects ", "lightGreen", Ct)).Value.ShouldBe(Work);
        (await new CreateCalendar(_store).ExecuteAsync(Me, " ", null, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await new CreateCalendar(_store).ExecuteAsync(Me, "x", "purple", Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public async Task Update_passes_name_and_colour_and_auto_clears_it()
    {
        _store.UpdateAsync(default, default, default, default, Ct).ReturnsForAnyArgs(Work);

        await new UpdateCalendar(_store).ExecuteAsync(Me, Work.Id, "Office", "auto", Ct);
        await new UpdateCalendar(_store).ExecuteAsync(Me, Work.Id, null, null, Ct);

        await _store.Received(1).UpdateAsync(Me, Work.Id, "Office", string.Empty, Ct);
        await _store.Received(1).UpdateAsync(Me, Work.Id, null, null, Ct);
        (await new UpdateCalendar(_store).ExecuteAsync(Me, Work.Id, "", null, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public async Task The_default_calendar_cannot_be_deleted()
    {
        _store.DeleteAsync(Me, Work.Id, Ct).Returns(Result.Success());

        (await new DeleteCalendar(_store).ExecuteAsync(Me, Personal.Id, Ct)).Error!.Code.ShouldBe("ErrorCannotDeleteDefaultCalendar");
        (await new DeleteCalendar(_store).ExecuteAsync(Me, Work.Id, Ct)).IsSuccess.ShouldBeTrue();
        (await new DeleteCalendar(_store).ExecuteAsync(Me, new CalendarId("x"), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task The_one_calendar_group_holds_every_calendar()
    {
        (await new ListGroupCalendars(_store).ExecuteAsync(Me, CalendarGroups.DefaultId, Ct)).Value.Count.ShouldBe(2);
        (await new ListGroupCalendars(_store).ExecuteAsync(Me, "other", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Events_in_a_calendar_pass_through_and_an_overlong_view_is_refused()
    {
        var events = Substitute.For<ICalendarEventsIn>();
        var start = DateTimeOffset.UnixEpoch;

        (await new CalendarEventsIn(events, NoRooms).ViewAsync(Me, Work.Id, start, start.AddYears(11), ItemQuery.Default, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await new CalendarEventsIn(events, NoRooms).InstancesAsync(Me, new EventId("s"), start, start.AddYears(11), ItemQuery.Default, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        await new CalendarEventsIn(events, NoRooms).InstancesAsync(Me, new EventId("s"), start, start.AddDays(7), ItemQuery.Default, Ct);
        await events.Received(1).InstancesAsync(Me, new EventId("s"), start, start.AddDays(7), ItemQuery.Default, Ct);
        await new CalendarEventsIn(events, NoRooms).ListAsync(Me, Work.Id, ItemQuery.Default, Ct);
        await events.Received(1).ListAsync(Me, Work.Id, ItemQuery.Default, Ct);
    }

    [Fact]
    public async Task Calendar_view_delta_needs_a_window_without_a_token()
    {
        var delta = Substitute.For<ICalendarViewDelta>();
        var start = DateTimeOffset.UnixEpoch;

        (await new SyncCalendarView(delta).ExecuteAsync(Me, null, null, null, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await new SyncCalendarView(delta).ExecuteAsync(Me, start, start.AddYears(11), null, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        await new SyncCalendarView(delta).ExecuteAsync(Me, null, null, new DeltaToken("t"), Ct);
        await delta.Received(1).DeltaAsync(Me, null, null, new DeltaToken("t"), Ct);
    }

    [Theory]
    [InlineData("#87d28e", "lightGreen")]
    [InlineData("#0082C9", "auto")]
    [InlineData(null, "auto")]
    public void Stored_hex_reads_as_graph_colour(string? hex, string color) => CalendarColors.FromHex(hex).ShouldBe(color);
}

public sealed class ReminderUseCaseTests
{
    private static readonly MailboxRef Me = new("me@example.com");
    private static readonly DateTimeOffset Now = new(2026, 12, 1, 9, 0, 0, TimeSpan.Zero);
    private readonly Nightware.Application.Calendar.ICalendarReader _reader = Substitute.For<Nightware.Application.Calendar.ICalendarReader>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CalendarEvent At(string id, int hour, bool reminder = true) =>
        new(new EventId(id), id, new DateTimeTimeZone(new DateTime(2026, 12, 1, hour, 0, 0), "UTC"), new DateTimeTimeZone(new DateTime(2026, 12, 1, hour, 30, 0), "UTC"))
        {
            IsReminderOn = reminder,
            ReminderMinutesBeforeStart = 15,
        };

    [Fact]
    public async Task The_view_holds_due_reminders_snoozed_at_their_snooze_time_without_dismissed_ones()
    {
        _reader.CalendarViewAsync(Me, Now, Now.AddDays(1), Arg.Any<ItemQuery>(), Ct).Returns(new Page<CalendarEvent>(
        [
            At("plain", 10),
            At("off", 11, reminder: false),
            At("dismissed", 12) with { ReminderAcknowledged = new DateTimeOffset(2026, 12, 1, 11, 45, 0, TimeSpan.Zero) },
            At("snoozed", 13) with { ReminderAcknowledged = Now, ReminderSnoozedUntil = Now.AddMinutes(10) },
        ]));

        var reminders = (await new GetReminderView(_reader).ExecuteAsync(Me, Now, Now.AddDays(1), Ct)).Value;

        reminders.Select(r => (r.EventId.Value, r.ReminderFireTime.DateTime.Hour, r.ReminderFireTime.DateTime.Minute)).ShouldBe([("snoozed", 9, 10), ("plain", 9, 45)]);
    }

    [Fact]
    public async Task Snooze_needs_a_time_and_a_backwards_window_is_refused()
    {
        var reminders = Substitute.For<ICalendarReminders>();

        (await new SnoozeReminder(reminders).ExecuteAsync(Me, new EventId("e"), null, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await new GetReminderView(_reader).ExecuteAsync(Me, Now, Now.AddDays(-1), Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        await new SnoozeReminder(reminders).ExecuteAsync(Me, new EventId("e"), new DateTimeTimeZone(new DateTime(2026, 12, 1, 9, 30, 0), "UTC"), Ct);
        await reminders.Received(1).SnoozeAsync(Me, new EventId("e"), Now.AddMinutes(30), Ct);
        await new DismissReminder(reminders).ExecuteAsync(Me, new EventId("e"), Ct);
        await reminders.Received(1).DismissAsync(Me, new EventId("e"), Ct);
    }
}

public sealed class EventAttachmentUseCaseTests
{
    private static readonly MailboxRef Me = new("me@example.com");
    private static readonly EventId Event = new("e");
    private readonly IEventAttachmentStore _store = Substitute.For<IEventAttachmentStore>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Add_needs_a_name_defaults_the_type_and_caps_the_size()
    {
        var attachment = new EventAttachment(new AttachmentId("a"), "x.bin", "application/octet-stream", 1);
        _store.AddAsync(Me, Event, "x.bin", "application/octet-stream", Arg.Any<ReadOnlyMemory<byte>>(), Ct).Returns(attachment);

        (await new EventAttachmentsUseCase(_store).AddAsync(Me, Event, " x.bin ", null, new byte[1], Ct)).Value.ShouldBe(attachment);
        (await new EventAttachmentsUseCase(_store).AddAsync(Me, Event, " ", null, new byte[1], Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await new EventAttachmentsUseCase(_store).AddAsync(Me, Event, "big", null, new byte[EventAttachmentsUseCase.MaxBytes + 1], Ct)).Error!.Kind.ShouldBe(ErrorKind.PayloadTooLarge);
    }

    [Fact]
    public async Task List_get_and_delete_pass_through()
    {
        var useCase = new EventAttachmentsUseCase(_store);

        await useCase.ListAsync(Me, Event, Ct);
        await useCase.GetAsync(Me, Event, new AttachmentId("a"), Ct);
        await useCase.DeleteAsync(Me, Event, new AttachmentId("a"), Ct);

        await _store.Received(1).ListAsync(Me, Event, Ct);
        await _store.Received(1).GetAsync(Me, Event, new AttachmentId("a"), Ct);
        await _store.Received(1).DeleteAsync(Me, Event, new AttachmentId("a"), Ct);
    }
}

public sealed class CalendarPermissionUseCaseTests
{
    private static readonly MailboxRef Me = new("me@example.com");
    private static readonly CalendarId Calendar = new("c");
    private static readonly CalendarShare Bob = new("p1", new EmailAddress("bob@example.com", "Bob"), CalendarRole.Read);
    private readonly ICalendarPermissionStore _store = Substitute.For<ICalendarPermissionStore>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Only_read_and_write_can_be_granted()
    {
        _store.GrantAsync(Me, Calendar, "bob@example.com", CalendarRole.Write, Ct).Returns(Bob with { Role = CalendarRole.Write });

        (await new CalendarPermissionsUseCase(_store).CreateAsync(Me, Calendar, " bob@example.com ", "Write", Ct)).Value.Role.ShouldBe(CalendarRole.Write);
        (await new CalendarPermissionsUseCase(_store).CreateAsync(Me, Calendar, "bob@example.com", "freeBusyRead", Ct)).Error!.Kind.ShouldBe(ErrorKind.Unsupported);
        (await new CalendarPermissionsUseCase(_store).CreateAsync(Me, Calendar, null, "read", Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public async Task Update_regrants_the_existing_sharee_with_the_new_role()
    {
        _store.ListAsync(Me, Calendar, Ct).Returns(Result<IReadOnlyList<CalendarShare>>.Success([Bob]));
        _store.GrantAsync(Me, Calendar, "bob@example.com", CalendarRole.Write, Ct).Returns(Bob with { Role = CalendarRole.Write });

        (await new CalendarPermissionsUseCase(_store).UpdateAsync(Me, Calendar, "p1", "write", Ct)).Value.Role.ShouldBe(CalendarRole.Write);
        (await new CalendarPermissionsUseCase(_store).UpdateAsync(Me, Calendar, "nope", "write", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }
}

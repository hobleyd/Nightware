using Nightware.Application.Calendar;
using Nightware.Application.Calendar.FreeBusy;
using Nightware.Application.Directory;
using Nightware.Domain.Calendar;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Calendar;

public sealed class FindMeetingTimesTests
{
    private static readonly MailboxRef Me = new("me@example.com");
    private const string Bob = "bob@example.com";
    private const string Carol = "carol@example.com";

    // Monday 2026-11-23, in UTC; working hours default to 08:00–17:00 Monday to Friday.
    private static readonly DateTimeOffset Monday = new(2026, 11, 23, 0, 0, 0, TimeSpan.Zero);

    private readonly IFreeBusyReader _freeBusy = Substitute.For<IFreeBusyReader>();
    private readonly IUserPreferencesStore _preferences = Substitute.For<IUserPreferencesStore>();
    private readonly TimeProvider _clock = Substitute.For<TimeProvider>();
    private readonly Dictionary<string, List<ScheduleItem>> _busy = new(StringComparer.OrdinalIgnoreCase);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public FindMeetingTimesTests()
    {
        _clock.GetUtcNow().Returns(Monday.AddDays(-3));
        _preferences.GetAsync(Me, Arg.Any<CancellationToken>()).Returns(Error.NotFound("none"));
        _freeBusy.GetAsync(Me, Arg.Any<IReadOnlyList<string>>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(call => Result<IReadOnlyList<FreeBusySchedule>>.Success(call.Arg<IReadOnlyList<string>>()
                .Select(a => a == "ghost@example.com"
                    ? new FreeBusySchedule(a, []) { ErrorMessage = "unknown" }
                    : new FreeBusySchedule(a, _busy.TryGetValue(a, out var items) ? items : []))
                .ToList()));
    }

    private void Busy(string address, int fromHour, int toHour, FreeBusyStatus status = FreeBusyStatus.Busy)
    {
        if (!_busy.TryGetValue(address, out var items))
        {
            _busy[address] = items = [];
        }

        items.Add(new ScheduleItem(Monday.AddHours(fromHour), Monday.AddHours(toHour), status));
    }

    private static MeetingTimeRequest Request(DateTimeOffset from, DateTimeOffset to, params MeetingAttendee[] attendees) =>
        new(attendees, TimeSpan.FromHours(1)) { TimeSlots = [new MeetingTimeSlot(DateTimeTimeZone.Utc(from), DateTimeTimeZone.Utc(to))], MaxCandidates = 20 };

    private Task<Result<MeetingTimeSuggestions>> Find(MeetingTimeRequest request) =>
        new FindMeetingTimes(_freeBusy, _preferences, _clock).ExecuteAsync(Me, request, Ct);

    [Fact]
    public async Task Suggestions_keep_to_working_hours_on_half_hour_boundaries()
    {
        var found = (await Find(Request(Monday.AddDays(-2).AddMinutes(10), Monday.AddHours(10)))).Value;

        found.EmptySuggestionsReason.ShouldBeEmpty();
        found.Suggestions.Select(s => s.Start.Hour + (s.Start.Minute / 60.0)).ShouldBe([8, 8.5, 9]);
        found.Suggestions.ShouldAllBe(s => s.Confidence == 100 && s.OrganizerAvailability == FreeBusyStatus.Free);
    }

    [Fact]
    public async Task Times_when_the_organizer_is_busy_are_skipped_and_the_rest_ranked_by_who_can_come()
    {
        Busy(Me.Address, 8, 9);
        Busy(Bob, 9, 10);
        Busy(Carol, 10, 11, FreeBusyStatus.Tentative);

        var found = (await Find(Request(Monday.AddHours(8), Monday.AddHours(12), new MeetingAttendee(Bob, AttendeeType.Required), new MeetingAttendee(Carol, AttendeeType.Optional)))).Value;

        found.Suggestions[0].Start.ShouldBe(Monday.AddHours(11));
        found.Suggestions[0].Confidence.ShouldBe(100);
        found.Suggestions.ShouldNotContain(s => s.Start < Monday.AddHours(9));
        var tentative = found.Suggestions.Single(s => s.Start == Monday.AddHours(10));
        tentative.Confidence.ShouldBe(83.33);
        tentative.Attendees.Single(a => a.Attendee.Address == Carol).Availability.ShouldBe(FreeBusyStatus.Tentative);

        // At 09:00 only optional Carol can come: 33% is below the default minimum of 50%.
        found.Suggestions.ShouldNotContain(s => s.Start == Monday.AddHours(9));
    }

    [Fact]
    public async Task Nothing_free_says_why()
    {
        Busy(Me.Address, 8, 17);
        (await Find(Request(Monday.AddHours(8), Monday.AddHours(17)))).Value.EmptySuggestionsReason.ShouldBe("organizerUnavailable");

        _busy.Clear();
        Busy(Bob, 8, 17, FreeBusyStatus.Oof);
        (await Find(Request(Monday.AddHours(8), Monday.AddHours(17), new MeetingAttendee(Bob, AttendeeType.Required)))).Value
            .EmptySuggestionsReason.ShouldBe("attendeesUnavailable");
    }

    [Fact]
    public async Task An_unknown_attendee_counts_half()
    {
        var found = (await Find(Request(Monday.AddHours(8), Monday.AddHours(9), new MeetingAttendee("ghost@example.com", AttendeeType.Required)))).Value;

        found.Suggestions.ShouldHaveSingleItem().Confidence.ShouldBe(50);
        found.Suggestions[0].Attendees.ShouldHaveSingleItem().Availability.ShouldBe(FreeBusyStatus.Unknown);
    }

    [Fact]
    public async Task Working_hours_follow_the_mailbox_and_activity_domain()
    {
        _preferences.GetAsync(Me, Arg.Any<CancellationToken>()).Returns(new UserPreferences("UTC", "en")
        {
            WorkingHours = new WorkingHours([DayOfWeek.Saturday], new TimeOnly(10, 0), new TimeOnly(12, 0), "UTC"),
        });
        var saturday = Monday.AddDays(-2);

        (await Find(Request(saturday, Monday.AddHours(23)))).Value.Suggestions.Select(s => s.Start.TimeOfDay.TotalHours).ShouldBe([10, 10.5, 11]);
        var personal = Request(saturday, Monday.AddHours(23)) with { ActivityDomain = MeetingActivityDomain.Personal };
        (await Find(personal)).Value.Suggestions.Count.ShouldBe(9);
        var any = Request(saturday, saturday.AddHours(3)) with { ActivityDomain = MeetingActivityDomain.Unrestricted };
        (await Find(any)).Value.Suggestions.Count.ShouldBe(5);
    }

    [Fact]
    public async Task The_window_defaults_to_the_next_two_days()
    {
        _clock.GetUtcNow().Returns(Monday.AddHours(15).AddMinutes(50));

        var found = await Find(new MeetingTimeRequest([], TimeSpan.FromHours(1)));

        found.Value.Suggestions[0].Start.ShouldBe(Monday.AddHours(16));
        found.Value.Suggestions.Count.ShouldBe(FindMeetingTimes.DefaultMaxCandidates);
    }

    [Fact]
    public async Task Slots_that_have_already_begun_are_not_suggested()
    {
        _clock.GetUtcNow().Returns(Monday.AddHours(9).AddMinutes(1));

        var found = (await Find(Request(Monday.AddHours(8), Monday.AddHours(12)))).Value;

        found.Suggestions.Select(s => s.Start).ShouldBe([Monday.AddHours(9.5), Monday.AddHours(10), Monday.AddHours(10.5), Monday.AddHours(11)]);
    }

    [Theory]
    [InlineData(0, null, null)]
    [InlineData(60, 0, null)]
    [InlineData(60, null, 101.0)]
    public async Task Bad_requests_are_400(int minutes, int? maxCandidates, double? minimum)
    {
        var request = new MeetingTimeRequest([], TimeSpan.FromMinutes(minutes)) { MaxCandidates = maxCandidates, MinimumAttendeePercentage = minimum };

        (await Find(request)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await Find(new MeetingTimeRequest([new MeetingAttendee("not an address", AttendeeType.Required)], TimeSpan.FromHours(1)))).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await Find(Request(Monday, Monday.AddDays(63)))).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }
}

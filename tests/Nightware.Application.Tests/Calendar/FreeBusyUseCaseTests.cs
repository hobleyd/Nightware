using Nightware.Application.Calendar;
using Nightware.Application.Calendar.FreeBusy;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Calendar;

public sealed class FreeBusyUseCaseTests
{
    private static readonly MailboxRef Me = new("me@example.com");
    private static readonly DateTimeOffset Start = new(2026, 6, 10, 0, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly IFreeBusyReader _freeBusy = Substitute.For<IFreeBusyReader>();

    private static ScheduleItem Item(int fromMinutes, int toMinutes, FreeBusyStatus status) =>
        new(Start.AddMinutes(fromMinutes), Start.AddMinutes(toMinutes), status);

    private static ScheduleRequest Request(params string[] schedules) =>
        new(schedules, DateTimeTimeZone.Utc(Start), DateTimeTimeZone.Utc(Start.AddHours(4)));

    [Fact]
    public void Availability_view_buckets_with_half_open_intervals()
    {
        var items = new[] { Item(60, 90, FreeBusyStatus.Busy), Item(150, 151, FreeBusyStatus.Tentative) };
        AvailabilityView.Compute(items, Start, Start.AddHours(4), TimeSpan.FromMinutes(30)).ShouldBe("00200100");
    }

    [Fact]
    public void Stronger_statuses_win_by_precedence_not_by_digit()
    {
        var items = new[]
        {
            Item(0, 60, FreeBusyStatus.WorkingElsewhere), Item(0, 30, FreeBusyStatus.Busy),
            Item(30, 60, FreeBusyStatus.Tentative), Item(60, 90, FreeBusyStatus.Busy), Item(60, 90, FreeBusyStatus.Oof),
            Item(90, 120, FreeBusyStatus.WorkingElsewhere), Item(90, 120, FreeBusyStatus.Free), Item(90, 120, FreeBusyStatus.Unknown),
        };
        AvailabilityView.Compute(items, Start, Start.AddHours(2), TimeSpan.FromMinutes(30)).ShouldBe("2134");
    }

    [Fact]
    public void A_partial_last_slot_counts_and_periods_outside_the_window_are_ignored()
    {
        var items = new[] { Item(-60, 10, FreeBusyStatus.Busy), Item(70, 200, FreeBusyStatus.Oof) };
        AvailabilityView.Compute(items, Start, Start.AddMinutes(75), TimeSpan.FromMinutes(30)).ShouldBe("203");
        AvailabilityView.Compute([], Start, Start, TimeSpan.FromMinutes(30)).ShouldBeEmpty();
        Should.Throw<ArgumentOutOfRangeException>(() => AvailabilityView.Compute([], Start, Start, TimeSpan.Zero));
    }

    [Fact]
    public async Task Schedules_come_back_in_request_order_with_views_and_errors()
    {
        _freeBusy.GetAsync(Me, Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "sam@example.com", "room@example.com", "x@example.com" })), Start, Start.AddHours(4), Ct)
            .Returns(Result<IReadOnlyList<FreeBusySchedule>>.Success(
            [
                new FreeBusySchedule("ROOM@example.com", [Item(0, 30, FreeBusyStatus.Busy), Item(300, 330, FreeBusyStatus.Busy)]),
                new FreeBusySchedule("sam@example.com", []) { ErrorMessage = "not found" },
            ]));

        var result = (await new GetSchedule(_freeBusy).ExecuteAsync(Me, Request("sam@example.com", "room@example.com", " x@example.com"), Ct)).Value;

        result.Select(s => s.ScheduleId).ShouldBe(["sam@example.com", "room@example.com", " x@example.com"]);
        result[0].ErrorMessage.ShouldBe("not found");
        result[1].AvailabilityView.ShouldBe("20000000");
        result[1].Items.ShouldHaveSingleItem(); // the period after the window is dropped
        result[2].ErrorMessage.ShouldNotBeNull();
    }

    [Fact]
    public async Task The_interval_is_honoured()
    {
        _freeBusy.GetAsync(Me, Arg.Any<IReadOnlyList<string>>(), Start, Start.AddHours(4), Ct)
            .Returns(Result<IReadOnlyList<FreeBusySchedule>>.Success([new FreeBusySchedule("a@example.com", [Item(60, 120, FreeBusyStatus.Busy)])]));

        var result = await new GetSchedule(_freeBusy).ExecuteAsync(Me, Request("a@example.com") with { IntervalMinutes = 60 }, Ct);

        result.Value.Single().AvailabilityView.ShouldBe("0200");
    }

    public static TheoryData<ScheduleRequest> InvalidRequests() => new()
    {
        Request(),
        Request("not-an-address"),
        Request([.. Enumerable.Range(0, 101).Select(i => $"u{i}@example.com")]),
        Request("a@example.com") with { IntervalMinutes = 1 },
        Request("a@example.com") with { End = DateTimeTimeZone.Utc(Start) },
        Request("a@example.com") with { End = DateTimeTimeZone.Utc(Start.AddDays(63)) },
        Request("a@example.com") with { Start = new DateTimeTimeZone(Start.DateTime, "Nowhere/Land") },
    };

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task Invalid_requests_are_rejected(ScheduleRequest request)
    {
        (await new GetSchedule(_freeBusy).ExecuteAsync(Me, request, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        await _freeBusy.DidNotReceiveWithAnyArgs().GetAsync(default, null!, default, default, Ct);
    }

    [Fact]
    public async Task Backend_failures_pass_through()
    {
        _freeBusy.GetAsync(Me, Arg.Any<IReadOnlyList<string>>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Ct).Returns(Error.Forbidden("no outbox"));
        (await new GetSchedule(_freeBusy).ExecuteAsync(Me, Request("a@example.com"), Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }
}

using Nightware.Application.Calendar;
using Nightware.Application.Directory;
using Nightware.Conformance.Tests.Harness;
using Nightware.Domain.Calendar;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Conformance.Tests.Arrangements;

/// <summary>
/// Phase 13 <c>findMeetingTimes</c> (ADR 0006 D6), against a capture from Microsoft 365: the captured
/// mailbox works 09:00–17:00 Sydney time, so a Saturday-to-Tuesday window first fits on Monday morning.
/// </summary>
public sealed class FindMeetingTimesArrangements : IConformanceArrangements
{
    private static readonly MailboxRef Me = new("me@example.com");

    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        yield return new ConformanceArrangement("calendar.find-meeting-times", static fakes =>
        {
            var weekdays = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };
            fakes.For<IUserPreferencesStore>().GetAsync(Me, Arg.Any<CancellationToken>()).Returns(new UserPreferences("AUS Eastern Standard Time", "en")
            {
                WorkingHours = new WorkingHours(weekdays, new TimeOnly(9, 0), new TimeOnly(17, 0), "AUS Eastern Standard Time"),
            });
            fakes.For<IFreeBusyReader>()
                .GetAsync(Me, Arg.Any<IReadOnlyList<string>>(), Arg.Any<DateTimeOffset>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
                .Returns(Result<IReadOnlyList<FreeBusySchedule>>.Success([new FreeBusySchedule(Me.Address, [])]));
        });
    }
}

using Nightware.Application.Calendar.Calendars;
using Nightware.Conformance.Tests.Harness;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Conformance.Tests.Arrangements;

/// <summary>Phase 13 calendar sharing (ADR 0006 D6), against captures from Microsoft 365.</summary>
public sealed class CalendarPermissionArrangements : IConformanceArrangements
{
    private static readonly MailboxRef Me = new("me@example.com");
    private static readonly CalendarId Calendar = new("AAMk-id-0004=");
    private static readonly EmailAddress Colleague = new("colleague@example.com", "Me Example");
    private static readonly CalendarShare Read = new("AAMk-id-0015=", Colleague, CalendarRole.Read);

    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        yield return new ConformanceArrangement("calendar.permissions.list", static fakes =>
            fakes.For<ICalendarPermissionStore>().ListAsync(Me, Calendar, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<CalendarShare>>.Success([])));

        yield return new ConformanceArrangement("calendar.permissions.create", static fakes =>
            fakes.For<ICalendarPermissionStore>().GrantAsync(Me, Calendar, "colleague@example.com", CalendarRole.Read, Arg.Any<CancellationToken>()).Returns(Read));

        yield return new ConformanceArrangement("calendar.permissions.update", static fakes =>
        {
            var store = fakes.For<ICalendarPermissionStore>();
            store.ListAsync(Me, Calendar, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<CalendarShare>>.Success([Read]));
            store.GrantAsync(Me, Calendar, "colleague@example.com", CalendarRole.Write, Arg.Any<CancellationToken>()).Returns(Read with { Role = CalendarRole.Write });
        });

        yield return new ConformanceArrangement("calendar.permissions.delete", static fakes =>
            fakes.For<ICalendarPermissionStore>().RevokeAsync(Me, Calendar, "AAMk-id-0015=", Arg.Any<CancellationToken>()).Returns(Result.Success()));
    }
}

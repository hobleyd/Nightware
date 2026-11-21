using Nightware.Application.Common.Time;
using Nightware.Application.Directory;
using Nightware.Domain.Calendar;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Calendar.FreeBusy;

/// <summary>
/// Graph <c>POST findMeetingTimes</c> (ADR 0006 D6): candidate slots on half-hour boundaries inside the
/// requested windows and the organizer's working hours, scored by how many attendees are free.
/// Free/busy comes from the same port as <c>getSchedule</c>; locations are not suggested.
/// </summary>
public sealed class FindMeetingTimes(IFreeBusyReader freeBusy, IUserPreferencesStore preferences, TimeProvider time)
{
    /// <summary>Exchange's working hours for a mailbox that has set none.</summary>
    public static readonly WorkingHours DefaultWorkingHours = new(
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday],
        new TimeOnly(8, 0), new TimeOnly(17, 0), GraphTimeZone.Utc);

    public const int DefaultMaxCandidates = 5;

    public const double DefaultMinimumAttendeePercentage = 50;

    private static readonly TimeSpan Step = TimeSpan.FromMinutes(30);

    /// <summary>Without a time constraint Graph looks ahead over the next two days.</summary>
    private static readonly TimeSpan DefaultLookAhead = TimeSpan.FromDays(2);

    public async Task<Result<MeetingTimeSuggestions>> ExecuteAsync(MailboxRef mailbox, MeetingTimeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Validate() is { } invalid)
        {
            return invalid;
        }

        var windows = Windows(request);
        if (windows.Max(w => w.End) - windows.Min(w => w.Start) > GetSchedule.MaxWindow)
        {
            return Error.Validation($"The time constraint cannot span more than {GetSchedule.MaxWindow.TotalDays} days.", "timeConstraint");
        }

        var hours = await WorkingHoursAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!hours.IsSuccess)
        {
            return hours.Error;
        }

        var attendees = request.Attendees
            .Where(a => a.Type != AttendeeType.Resource)
            .Select(a => a with { Address = a.Address.Trim() })
            .DistinctBy(a => a.Address, StringComparer.OrdinalIgnoreCase)
            .Where(a => !string.Equals(a.Address, mailbox.Address, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var schedules = await freeBusy.GetAsync(
            mailbox, [mailbox.Address, .. attendees.Select(a => a.Address)], windows.Min(w => w.Start), windows.Max(w => w.End), cancellationToken).ConfigureAwait(false);
        if (!schedules.IsSuccess)
        {
            return schedules.Error;
        }

        var byAddress = schedules.Value
            .GroupBy(s => s.ScheduleId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var minimum = request.MinimumAttendeePercentage ?? DefaultMinimumAttendeePercentage;
        var suggestions = new List<MeetingTimeSuggestion>();
        var organizerEverFree = false;
        var anyUnknown = false;
        // A slot that has already begun cannot be suggested.
        var now = time.GetUtcNow();
        foreach (var (start, end) in Candidates(windows, request.Duration, request.ActivityDomain, hours.Value).Where(c => c.Start >= now))
        {
            var organizer = Availability(byAddress, mailbox.Address, start, end);
            var availability = attendees.Select(a => new AttendeeAvailability(a, Availability(byAddress, a.Address, start, end))).ToList();
            anyUnknown |= availability.Any(a => a.Availability == FreeBusyStatus.Unknown);
            if (!request.IsOrganizerOptional && organizer != FreeBusyStatus.Free)
            {
                continue;
            }

            organizerEverFree = true;
            var confidence = Confidence(availability);
            if (confidence >= minimum)
            {
                suggestions.Add(new MeetingTimeSuggestion(start, end, confidence, organizer, availability));
            }
        }

        var chosen = suggestions
            .OrderByDescending(s => s.Confidence)
            .ThenBy(s => s.Start)
            .Take(request.MaxCandidates ?? DefaultMaxCandidates)
            .ToList();
        var reason = chosen.Count > 0 ? string.Empty
            : !organizerEverFree && !request.IsOrganizerOptional ? "organizerUnavailable"
            : anyUnknown ? "attendeesUnavailableOrUnknown"
            : "attendeesUnavailable";
        return new MeetingTimeSuggestions(chosen, reason);
    }

    private List<(DateTimeOffset Start, DateTimeOffset End)> Windows(MeetingTimeRequest request)
    {
        if (request.TimeSlots is { Count: > 0 } slots)
        {
            return slots.Select(s => (CalendarRules.ToInstant(s.Start), CalendarRules.ToInstant(s.End))).ToList();
        }

        var now = time.GetUtcNow();
        return [(now, now + DefaultLookAhead)];
    }

    private async Task<Result<WorkingHours>> WorkingHoursAsync(MailboxRef mailbox, CancellationToken cancellationToken)
    {
        var stored = await preferences.GetAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!stored.IsSuccess)
        {
            return stored.Error.Kind == ErrorKind.NotFound ? DefaultWorkingHours : stored.Error;
        }

        // Its own, or Exchange's defaults in the mailbox's zone: what GET mailboxSettings/workingHours reports too.
        return MailboxSettings.MailboxSettingsComposer.WorkingHours(stored.Value);
    }

    /// <summary>Starts on half-hour boundaries that fit the window and, for work/personal, the working day.</summary>
    internal static IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> Candidates(
        IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> windows, TimeSpan duration, MeetingActivityDomain domain, WorkingHours hours)
    {
        GraphTimeZone.TryFind(hours.TimeZone, out var zone);
        var seen = new HashSet<DateTimeOffset>();
        foreach (var (windowStart, windowEnd) in windows.OrderBy(w => w.Start))
        {
            var ticks = Step.Ticks;
            var start = new DateTimeOffset((windowStart.UtcTicks + ticks - 1) / ticks * ticks, TimeSpan.Zero);
            for (; start + duration <= windowEnd; start += Step)
            {
                var end = start + duration;
                if (seen.Add(start) && Allowed(start, end, domain, hours, zone))
                {
                    yield return (start, end);
                }
            }
        }
    }

    private static bool Allowed(DateTimeOffset start, DateTimeOffset end, MeetingActivityDomain domain, WorkingHours hours, TimeZoneInfo zone)
    {
        if (domain == MeetingActivityDomain.Unrestricted)
        {
            return true;
        }

        var from = GraphTimeZone.ToWallClock(start, zone);
        var to = GraphTimeZone.ToWallClock(end, zone);
        if (from.Date != to.Date && to.TimeOfDay != TimeSpan.Zero)
        {
            return false;
        }

        // Work keeps to the working days; personal also allows the rest of the week, at the same hours.
        if (domain == MeetingActivityDomain.Work && !hours.DaysOfWeek.Contains(from.DayOfWeek))
        {
            return false;
        }

        var endOfDay = to.Date > from.Date ? TimeOnly.MaxValue : TimeOnly.FromDateTime(to);
        return TimeOnly.FromDateTime(from) >= hours.StartTime && endOfDay <= hours.EndTime;
    }

    private static FreeBusyStatus Availability(Dictionary<string, FreeBusySchedule> schedules, string address, DateTimeOffset start, DateTimeOffset end)
    {
        if (!schedules.TryGetValue(address, out var schedule) || schedule.ErrorMessage is not null)
        {
            return FreeBusyStatus.Unknown;
        }

        var overlapping = schedule.Items.Where(i => i.Start < end && i.End > start && i.Status != FreeBusyStatus.Free).Select(i => i.Status).ToList();
        return overlapping.Count == 0 ? FreeBusyStatus.Free : overlapping.MaxBy(Severity);
    }

    /// <summary>How far a status keeps someone from a meeting (the enum's order is Graph's, not this).</summary>
    private static int Severity(FreeBusyStatus status) => status switch
    {
        FreeBusyStatus.Oof => 4,
        FreeBusyStatus.Busy => 3,
        FreeBusyStatus.Tentative => 2,
        FreeBusyStatus.WorkingElsewhere => 1,
        _ => 0,
    };

    /// <summary>
    /// The share of attendees who can come, as a percentage: free counts fully, tentative or unknown by
    /// half; optional attendees weigh half as much as required ones. No attendees is 100.
    /// </summary>
    internal static double Confidence(IReadOnlyList<AttendeeAvailability> availability)
    {
        if (availability.Count == 0)
        {
            return 100;
        }

        double total = 0, score = 0;
        foreach (var a in availability)
        {
            var weight = a.Attendee.Type == AttendeeType.Optional ? 0.5 : 1;
            total += weight;
            score += weight * a.Availability switch
            {
                FreeBusyStatus.Free => 1,
                FreeBusyStatus.Tentative or FreeBusyStatus.Unknown => 0.5,
                _ => 0,
            };
        }

        return Math.Round(100 * score / total, 2);
    }
}

/// <summary>Graph <c>activityDomain</c>.</summary>
public enum MeetingActivityDomain
{
    Work,
    Personal,
    Unrestricted,
}

/// <summary>One person to find a time with.</summary>
public sealed record MeetingAttendee(string Address, AttendeeType Type);

public sealed record MeetingTimeSlot(DateTimeTimeZone Start, DateTimeTimeZone End);

/// <summary>The body of a <c>findMeetingTimes</c> request.</summary>
public sealed record MeetingTimeRequest(IReadOnlyList<MeetingAttendee> Attendees, TimeSpan Duration)
{
    /// <summary>One free/busy query covers the organizer and the attendees.</summary>
    public const int MaxAttendees = GetSchedule.MaxSchedules - 1;

    public IReadOnlyList<MeetingTimeSlot>? TimeSlots { get; init; }

    public MeetingActivityDomain ActivityDomain { get; init; } = MeetingActivityDomain.Work;

    public int? MaxCandidates { get; init; }

    public bool IsOrganizerOptional { get; init; }

    public double? MinimumAttendeePercentage { get; init; }

    internal Error? Validate()
    {
        if (Duration <= TimeSpan.Zero || Duration > TimeSpan.FromDays(1))
        {
            return Error.Validation("meetingDuration must be more than zero and at most a day.", "meetingDuration");
        }

        if (Attendees.Count > MaxAttendees)
        {
            return Error.Validation($"At most {MaxAttendees} attendees can be considered at once.", "attendees");
        }

        if (Attendees.FirstOrDefault(a => !CalendarRules.IsAddress(a.Address?.Trim())) is { } bad)
        {
            return Error.Validation($"'{bad.Address}' is not a valid email address.", "attendees");
        }

        if (MaxCandidates is < 1 or > 100)
        {
            return Error.Validation("maxCandidates must be between 1 and 100.", "maxCandidates");
        }

        if (MinimumAttendeePercentage is < 0 or > 100)
        {
            return Error.Validation("minimumAttendeePercentage must be between 0 and 100.", "minimumAttendeePercentage");
        }

        foreach (var slot in TimeSlots ?? [])
        {
            var error = CalendarRules.KnownZone(slot.Start, "timeSlots") ?? CalendarRules.KnownZone(slot.End, "timeSlots")
                ?? CalendarRules.Ordered(slot.Start, slot.End, "timeSlots", allowEmpty: false);
            if (error is not null)
            {
                return error;
            }
        }

        return null;
    }
}

public sealed record AttendeeAvailability(MeetingAttendee Attendee, FreeBusyStatus Availability);

public sealed record MeetingTimeSuggestion(DateTimeOffset Start, DateTimeOffset End, double Confidence, FreeBusyStatus OrganizerAvailability, IReadOnlyList<AttendeeAvailability> Attendees);

/// <param name="EmptySuggestionsReason">Empty when there are suggestions; otherwise Graph's reason.</param>
public sealed record MeetingTimeSuggestions(IReadOnlyList<MeetingTimeSuggestion> Suggestions, string EmptySuggestionsReason);

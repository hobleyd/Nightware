using System.Net;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Features.Calendar.Dto;

/// <summary>
/// Maps request bodies to Domain writes. Shape problems (missing times, attendees without an address)
/// are 400s here; semantic checks (known zones, end after start) belong to the use cases.
/// </summary>
public static class EventRequestMapper
{
    public static Result<NewEvent> ToNewEvent(EventWriteDto? body)
    {
        if (body is null)
        {
            return Error.Validation("A request body is required.");
        }

        if (body.Start is null || body.End is null)
        {
            return Error.Validation("An event needs a start and an end.", body.Start is null ? "start" : "end");
        }

        var attendees = ToAttendees(body.Attendees);
        if (!attendees.IsSuccess)
        {
            return attendees.Error;
        }

        var recurrence = ToRecurrence(body.Recurrence);
        if (!recurrence.IsSuccess)
        {
            return recurrence.Error;
        }

        var location = ToLocation(body.Location);
        return new NewEvent(body.Subject, ToDomain(body.Start), ToDomain(body.End))
        {
            IsAllDay = body.IsAllDay ?? false,
            BodyHtml = ToHtml(body.Body),
            Location = location is { DisplayName.Length: > 0 } ? location : null,
            Locations = ToLocations(body.Locations),
            Attendees = attendees.Value ?? [],
            ShowAs = body.ShowAs ?? ShowAs.Busy,
            Recurrence = recurrence.Value,
            IsReminderOn = body.IsReminderOn ?? true,
            ReminderMinutesBeforeStart = body.ReminderMinutesBeforeStart ?? 15,
            Categories = body.Categories ?? [],
            IsOnlineMeeting = body.IsOnlineMeeting == true,
        };
    }

    public static Result<EventPatch> ToPatch(EventWriteDto? body)
    {
        if (body is null)
        {
            return Error.Validation("A request body is required.");
        }

        var attendees = ToAttendees(body.Attendees);
        if (!attendees.IsSuccess)
        {
            return attendees.Error;
        }

        var recurrence = ToRecurrence(body.Recurrence);
        if (!recurrence.IsSuccess)
        {
            return recurrence.Error;
        }

        return new EventPatch
        {
            Subject = body.Subject,
            Start = body.Start is null ? null : ToDomain(body.Start),
            End = body.End is null ? null : ToDomain(body.End),
            IsAllDay = body.IsAllDay,
            BodyHtml = ToHtml(body.Body),

            // An empty display name clears the location (NightMail's full edit sends {displayName: ""}).
            Location = ToLocation(body.Location),
            Locations = body.Locations is null ? null : ToLocations(body.Locations),
            Attendees = attendees.Value,
            ShowAs = body.ShowAs,
            Recurrence = recurrence.Value,
            ClearRecurrence = body.RecurrenceSent && body.Recurrence is null,
            IsReminderOn = body.IsReminderOn,
            ReminderMinutesBeforeStart = body.ReminderMinutesBeforeStart,
            Categories = body.Categories,
            IsOnlineMeeting = body.IsOnlineMeeting,
        };
    }

    public static Result<TimeSlot?> ToTimeSlot(TimeSlotDto? slot)
    {
        if (slot is null)
        {
            return Result<TimeSlot?>.Success(null);
        }

        return slot.Start is null || slot.End is null
            ? Error.Validation("A time slot needs a start and an end.", "proposedNewTime")
            : new TimeSlot(ToDomain(slot.Start), ToDomain(slot.End));
    }

    public static Result<IReadOnlyList<EmailAddress>> ToAddresses(IReadOnlyList<RecipientDto>? recipients, string target)
    {
        var addresses = new List<EmailAddress>();
        foreach (var recipient in recipients ?? [])
        {
            if (string.IsNullOrWhiteSpace(recipient.EmailAddress?.Address))
            {
                return Error.Validation("Every recipient needs an email address.", target);
            }

            addresses.Add(new EmailAddress(recipient.EmailAddress.Address.Trim(), NullIfBlank(recipient.EmailAddress.Name)));
        }

        return addresses;
    }

    public static DateTimeTimeZone ToDomain(DateTimeTimeZoneDto value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new DateTimeTimeZone(DateTime.SpecifyKind(value.DateTime, DateTimeKind.Unspecified), string.IsNullOrWhiteSpace(value.TimeZone) ? "UTC" : value.TimeZone.Trim());
    }

    private static Result<IReadOnlyList<Attendee>?> ToAttendees(IReadOnlyList<AttendeeInputDto>? attendees)
    {
        if (attendees is null)
        {
            return Result<IReadOnlyList<Attendee>?>.Success(null);
        }

        var result = new List<Attendee>(attendees.Count);
        foreach (var attendee in attendees)
        {
            if (string.IsNullOrWhiteSpace(attendee.EmailAddress?.Address))
            {
                return Error.Validation("Every attendee needs an email address.", "attendees");
            }

            result.Add(new Attendee(
                new EmailAddress(attendee.EmailAddress.Address.Trim(), NullIfBlank(attendee.EmailAddress.Name)),
                attendee.Type ?? AttendeeType.Required,
                new ResponseStatus(ResponseType.None)));
        }

        return result;
    }

    private static Result<PatternedRecurrence?> ToRecurrence(PatternedRecurrenceInputDto? recurrence)
    {
        if (recurrence is null)
        {
            return Result<PatternedRecurrence?>.Success(null);
        }

        if (recurrence.Pattern?.Type is not { } type || recurrence.Range?.Type is not { } rangeType || recurrence.Range.StartDate is not { } startDate)
        {
            return Error.Validation("A recurrence needs a pattern type, a range type and a start date.", "recurrence");
        }

        var pattern = recurrence.Pattern;
        var range = recurrence.Range;
        if (rangeType == RecurrenceRangeType.EndDate && range.EndDate is null)
        {
            return Error.Validation("A recurrence that ends on a date needs an endDate.", "recurrence");
        }

        if (rangeType == RecurrenceRangeType.Numbered && range.NumberOfOccurrences is not > 0)
        {
            return Error.Validation("A numbered recurrence needs numberOfOccurrences.", "recurrence");
        }

        return new PatternedRecurrence(
            new RecurrencePattern(type, Math.Max(1, pattern.Interval ?? 1))
            {
                DaysOfWeek = pattern.DaysOfWeek ?? [],
                DayOfMonth = pattern.DayOfMonth ?? 0,
                Month = pattern.Month ?? 0,
                Index = pattern.Index,
                FirstDayOfWeek = pattern.FirstDayOfWeek ?? DayOfWeek.Sunday,
            },
            new RecurrenceRange(rangeType, startDate)
            {
                EndDate = range.EndDate,
                NumberOfOccurrences = range.NumberOfOccurrences ?? 0,
                RecurrenceTimeZone = NullIfBlank(range.RecurrenceTimeZone),
            });
    }

    private static Location? ToLocation(LocationDto? location) =>
        location is null
            ? null
            : new Location(location.DisplayName?.Trim() ?? string.Empty) { LocationEmailAddress = NullIfBlank(location.LocationEmailAddress) };

    private static List<Location> ToLocations(IReadOnlyList<LocationDto>? locations) =>
        (locations ?? []).Select(ToLocation).OfType<Location>().Where(l => l.DisplayName.Length > 0 || l.LocationEmailAddress is not null).ToList();

    /// <summary>Event bodies are stored as HTML (DESCRIPTION is derived from it); NightMail sends plain text.</summary>
    private static string? ToHtml(ItemBodyDto? body)
    {
        if (body?.Content is null)
        {
            return null;
        }

        return string.Equals(body.ContentType, "html", StringComparison.OrdinalIgnoreCase)
            ? body.Content
            : WebUtility.HtmlEncode(body.Content.Replace("\r\n", "\n", StringComparison.Ordinal)).Replace("\n", "<br>", StringComparison.Ordinal);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

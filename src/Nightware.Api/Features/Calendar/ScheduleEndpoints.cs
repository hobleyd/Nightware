using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Nightware.Api.Features.Calendar.Dto;
using Nightware.Api.Graph;
using Nightware.Api.Graph.Routing;
using Nightware.Application.Calendar.FreeBusy;
using Nightware.Application.Common.Query;
using Nightware.Application.Common.Time;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Features.Calendar;

/// <summary><c>POST calendar/getSchedule</c>: free/busy for people and rooms; <c>POST findMeetingTimes</c> over it.</summary>
internal static class ScheduleEndpoints
{
    public const string ContextFragment = "Collection(microsoft.graph.scheduleInformation)";

    public static void Map(RouteGroupBuilder graph)
    {
        foreach (var scope in graph.MapMailboxGroups())
        {
            scope.MapPost("/calendar/getSchedule", GetScheduleAsync);
            scope.MapPost("/findMeetingTimes", FindMeetingTimesAsync);
        }
    }

    private static async Task<IResult> GetScheduleAsync(
        Mailbox mailbox, ScheduleRequestDto? body, PreferHeader prefer, HttpContext http, GetSchedule getSchedule, CancellationToken cancellationToken)
    {
        if (body?.StartTime is null || body.EndTime is null)
        {
            return GraphResults.Error(Error.Validation("startTime and endTime are required.", body?.StartTime is null ? "startTime" : "endTime"));
        }

        var request = new ScheduleRequest(
            body.Schedules ?? [],
            EventRequestMapper.ToDomain(body.StartTime),
            EventRequestMapper.ToDomain(body.EndTime),
            body.AvailabilityViewInterval);
        var result = await getSchedule.ExecuteAsync(mailbox.Ref, request, cancellationToken).ConfigureAwait(false);
        return GraphResults.Match(result, schedules =>
        {
            // Graph answers in the Prefer zone, else in the zone the window was given in (NightMail: UTC).
            var (zone, label) = OutputZone(prefer, request.Start.TimeZone);
            prefer.MarkTimeZoneApplied(http.Response);
            return GraphResults.Collection(schedules.Select(s => ToDto(s, zone, label)), ContextFragment, ItemQuery.Default);
        });
    }

    private static async Task<IResult> FindMeetingTimesAsync(
        Mailbox mailbox, MeetingTimeRequestDto? body, PreferHeader prefer, HttpContext http, FindMeetingTimes find, CancellationToken cancellationToken)
    {
        var request = ToRequest(body ?? new MeetingTimeRequestDto());
        if (!request.IsSuccess)
        {
            return GraphResults.Error(request.Error);
        }

        var result = await find.ExecuteAsync(mailbox.Ref, request.Value, cancellationToken).ConfigureAwait(false);
        return GraphResults.Match(result, found =>
        {
            var (zone, label) = OutputZone(prefer, request.Value.TimeSlots is [var first, ..] ? first.Start.TimeZone : GraphTimeZone.Utc);
            prefer.MarkTimeZoneApplied(http.Response);
            var dto = new MeetingTimeSuggestionsResultDto
            {
                EmptySuggestionsReason = found.EmptySuggestionsReason,
                MeetingTimeSuggestions = found.Suggestions.Select(s => new MeetingTimeSuggestionDto
                {
                    Confidence = s.Confidence,
                    OrganizerAvailability = s.OrganizerAvailability,
                    AttendeeAvailability = s.Attendees.Select(a => new AttendeeAvailabilityDto
                    {
                        Attendee = new AttendeeBaseDto { Type = a.Attendee.Type, EmailAddress = new EmailAddressDto { Address = a.Attendee.Address } },
                        Availability = a.Availability,
                    }).ToList(),
                    Locations = [],
                    MeetingTimeSlot = new TimeSlotDto { Start = EventDtoMapper.ToDto(s.Start, zone, label), End = EventDtoMapper.ToDto(s.End, zone, label) },
                }).ToList(),
            };
            return GraphResults.Entity(dto, "microsoft.graph.meetingTimeSuggestionsResult");
        });
    }

    private static Result<MeetingTimeRequest> ToRequest(MeetingTimeRequestDto body)
    {
        var duration = TimeSpan.FromMinutes(30);
        if (!string.IsNullOrWhiteSpace(body.MeetingDuration))
        {
            try
            {
                duration = System.Xml.XmlConvert.ToTimeSpan(body.MeetingDuration);
            }
            catch (FormatException)
            {
                return Error.Validation($"'{body.MeetingDuration}' is not an ISO 8601 duration.", "meetingDuration");
            }
        }

        var domain = body.TimeConstraint?.ActivityDomain switch
        {
            null or "" => MeetingActivityDomain.Work,
            var d when Enum.TryParse<MeetingActivityDomain>(d, ignoreCase: true, out var parsed) => parsed,
            _ => (MeetingActivityDomain?)null,
        };
        if (domain is null)
        {
            return Error.Validation("activityDomain must be work, personal or unrestricted.", "activityDomain");
        }

        var slots = new List<MeetingTimeSlot>();
        foreach (var slot in body.TimeConstraint?.TimeSlots ?? [])
        {
            if (slot.Start is null || slot.End is null)
            {
                return Error.Validation("Every time slot needs a start and an end.", "timeSlots");
            }

            slots.Add(new MeetingTimeSlot(EventRequestMapper.ToDomain(slot.Start), EventRequestMapper.ToDomain(slot.End)));
        }

        var attendees = (body.Attendees ?? []).Select(a => new MeetingAttendee(a.EmailAddress?.Address ?? string.Empty, a.Type)).ToList();
        return new MeetingTimeRequest(attendees, duration)
        {
            TimeSlots = slots,
            ActivityDomain = domain.Value,
            MaxCandidates = body.MaxCandidates,
            IsOrganizerOptional = body.IsOrganizerOptional ?? false,
            MinimumAttendeePercentage = body.MinimumAttendeePercentage,
        };
    }

    private static (TimeZoneInfo Zone, string Label) OutputZone(PreferHeader prefer, string requestZone)
    {
        if (prefer.TimeZoneName is not null)
        {
            return (prefer.TimeZone, prefer.TimeZoneLabel);
        }

        return GraphTimeZone.TryFind(requestZone, out var zone) ? (zone, requestZone) : (TimeZoneInfo.Utc, GraphTimeZone.Utc);
    }

    private static ScheduleInformationDto ToDto(ScheduleInformation schedule, TimeZoneInfo zone, string label) => new()
    {
        ScheduleId = schedule.ScheduleId,
        AvailabilityView = schedule.AvailabilityView,
        ScheduleItems = schedule.Items.Select(i => new ScheduleItemDto
        {
            IsPrivate = i.IsPrivate,
            Status = i.Status,
            Subject = i.Subject,
            Location = i.Location,
            Start = EventDtoMapper.ToDto(i.Start, zone, label),
            End = EventDtoMapper.ToDto(i.End, zone, label),
        }).ToList(),
        Error = schedule.ErrorMessage is null ? null : new FreeBusyErrorDto { Message = schedule.ErrorMessage, ResponseCode = "error" },
    };
}

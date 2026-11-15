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

/// <summary><c>POST calendar/getSchedule</c>: free/busy for people and rooms.</summary>
internal static class ScheduleEndpoints
{
    public const string ContextFragment = "Collection(microsoft.graph.scheduleInformation)";

    public static void Map(RouteGroupBuilder graph) =>
        graph.MapMailboxPost("/calendar/getSchedule", GetScheduleAsync);

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

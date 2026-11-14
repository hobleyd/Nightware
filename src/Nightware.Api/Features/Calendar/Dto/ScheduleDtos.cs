using System.Text.Json.Serialization;
using Nightware.Domain.Calendar;

namespace Nightware.Api.Features.Calendar.Dto;

/// <summary>The body of <c>POST calendar/getSchedule</c>.</summary>
public sealed record ScheduleRequestDto
{
    public IReadOnlyList<string>? Schedules { get; init; }

    public DateTimeTimeZoneDto? StartTime { get; init; }

    public DateTimeTimeZoneDto? EndTime { get; init; }

    public int? AvailabilityViewInterval { get; init; }
}

/// <summary>Graph <c>scheduleItem</c>. Plain free/busy has no subject or location, so those are omitted when unknown.</summary>
public sealed record ScheduleItemDto
{
    public bool IsPrivate { get; init; }

    public FreeBusyStatus Status { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Subject { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Location { get; init; }

    public DateTimeTimeZoneDto? Start { get; init; }

    public DateTimeTimeZoneDto? End { get; init; }
}

/// <summary>Graph <c>freeBusyError</c>.</summary>
public sealed record FreeBusyErrorDto
{
    public string? Message { get; init; }

    public string? ResponseCode { get; init; }
}

/// <summary>Graph <c>scheduleInformation</c>.</summary>
public sealed record ScheduleInformationDto
{
    public string? ScheduleId { get; init; }

    public string? AvailabilityView { get; init; }

    public IReadOnlyList<ScheduleItemDto>? ScheduleItems { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FreeBusyErrorDto? Error { get; init; }
}

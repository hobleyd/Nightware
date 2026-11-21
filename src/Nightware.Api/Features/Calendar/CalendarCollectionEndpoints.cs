using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Nightware.Api.Features.Calendar.Dto;
using Nightware.Api.Graph;
using Nightware.Api.Graph.OData;
using Nightware.Api.Graph.Routing;
using Nightware.Application.Calendar.Calendars;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Features.Calendar;

/// <summary>Graph <c>calendar</c>.</summary>
public sealed record CalendarDto
{
    public string? Id { get; init; }

    public string? Name { get; init; }

    public string Color { get; init; } = CalendarColors.Auto;

    public string HexColor { get; init; } = string.Empty;

    public string GroupClassId { get; init; } = CalendarGroups.ClassId;

    public bool IsDefaultCalendar { get; init; }

    public bool CanShare { get; init; }

    public bool CanViewPrivateItems { get; init; } = true;

    public bool IsShared { get; init; }

    public bool IsSharedWithMe { get; init; }

    public bool CanEdit { get; init; }

    public IReadOnlyList<string> AllowedOnlineMeetingProviders { get; init; } = [];

    public string DefaultOnlineMeetingProvider { get; init; } = "unknown";

    public bool IsTallyingResponses { get; init; }

    public bool IsRemovable { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CalendarOwnerDto? Owner { get; init; }

    public static CalendarDto From(EventCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(calendar);
        return new CalendarDto
        {
            Id = calendar.Id.Value,
            Name = calendar.Name,
            Color = CalendarColors.FromHex(calendar.HexColor),
            HexColor = calendar.HexColor ?? string.Empty,
            IsDefaultCalendar = calendar.IsDefault,
            CanShare = calendar.CanEdit && !calendar.IsSharedWithMe,
            IsShared = calendar.IsShared,
            IsSharedWithMe = calendar.IsSharedWithMe,
            CanEdit = calendar.CanEdit,
            IsTallyingResponses = calendar.IsDefault,
            IsRemovable = calendar.IsRemovable,
            Owner = calendar.Owner is { } owner ? new CalendarOwnerDto { Name = owner.Name ?? owner.Address, Address = owner.Address } : null,
        };
    }
}

/// <summary>Graph <c>emailAddress</c> as a calendar's owner.</summary>
public sealed record CalendarOwnerDto
{
    public string? Name { get; init; }

    public string? Address { get; init; }
}

/// <summary>Graph <c>calendarGroup</c>.</summary>
public sealed record CalendarGroupDto
{
    public string Id { get; init; } = CalendarGroups.DefaultId;

    public string Name { get; init; } = CalendarGroups.DefaultName;

    public string ClassId { get; init; } = CalendarGroups.ClassId;
}

/// <summary>Request body of <c>POST</c>/<c>PATCH calendars</c>.</summary>
public sealed record CalendarWriteDto
{
    public string? Name { get; init; }

    public string? Color { get; init; }
}

/// <summary>A mailbox's calendars and calendar groups (ADR 0006 D6, phase 13).</summary>
internal static class CalendarCollectionEndpoints
{
    public static void Map(RouteGroupBuilder graph)
    {
        foreach (var scope in graph.MapMailboxGroups())
        {
            scope.MapGet("/calendars", ListAsync);
            scope.MapPost("/calendars", CreateAsync);
            scope.MapGet("/calendar", GetDefaultAsync);
            scope.MapGet("/calendars/{calendarId}", GetAsync);
            scope.MapPatch("/calendars/{calendarId}", UpdateAsync);
            scope.MapDelete("/calendars/{calendarId}", DeleteAsync);
            scope.MapGet("/calendars/{calendarId}/events", ListEventsAsync);
            scope.MapPost("/calendars/{calendarId}/events", CreateEventAsync);
            scope.MapGet("/calendars/{calendarId}/calendarView", CalendarViewAsync);
            scope.MapGet("/events/{eventId}/instances", InstancesAsync);
            scope.MapGet("/calendarView/delta", CalendarViewDeltaAsync);
            scope.MapGet("/calendarGroups", ListGroupsAsync);
            scope.MapGet("/calendarGroups/{groupId}/calendars", ListGroupCalendarsAsync);
        }
    }

    private static string Calendars(Mailbox mailbox) => $"{mailbox.ContextSegment}/calendars";

    private static async Task<IResult> ListAsync(Mailbox mailbox, ODataQuery odata, ListCalendars useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(await useCase.ExecuteAsync(mailbox.Ref, cancellationToken).ConfigureAwait(false),
            list => GraphResults.Collection(list.Select(CalendarDto.From), Calendars(mailbox), odata.Query));

    private static async Task<IResult> GetAsync(Mailbox mailbox, string calendarId, ODataQuery odata, GetCalendar useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(await useCase.ExecuteAsync(mailbox.Ref, new CalendarId(calendarId), cancellationToken).ConfigureAwait(false),
            c => GraphResults.Entity(CalendarDto.From(c), Calendars(mailbox), odata.Query.Select));

    private static async Task<IResult> GetDefaultAsync(Mailbox mailbox, ODataQuery odata, GetCalendar useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(await useCase.ExecuteAsync(mailbox.Ref, null, cancellationToken).ConfigureAwait(false),
            c => GraphResults.Entity(CalendarDto.From(c), $"{mailbox.ContextSegment}/calendar", odata.Query.Select));

    private static async Task<IResult> CreateAsync(Mailbox mailbox, [FromBody] CalendarWriteDto? body, CreateCalendar useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(await useCase.ExecuteAsync(mailbox.Ref, body?.Name, body?.Color, cancellationToken).ConfigureAwait(false),
            c => GraphResults.CreatedAt(CalendarDto.From(c), Calendars(mailbox), $"{mailbox.ContextSegment}/calendars('{Uri.EscapeDataString(c.Id.Value)}')"));

    private static async Task<IResult> UpdateAsync(
        Mailbox mailbox, string calendarId, ODataQuery odata, [FromBody] CalendarWriteDto? body, UpdateCalendar useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(await useCase.ExecuteAsync(mailbox.Ref, new CalendarId(calendarId), body?.Name, body?.Color, cancellationToken).ConfigureAwait(false),
            c => GraphResults.Entity(CalendarDto.From(c), Calendars(mailbox), odata.Query.Select));

    private static async Task<IResult> DeleteAsync(Mailbox mailbox, string calendarId, DeleteCalendar useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(await useCase.ExecuteAsync(mailbox.Ref, new CalendarId(calendarId), cancellationToken).ConfigureAwait(false));

    private static async Task<IResult> ListEventsAsync(
        Mailbox mailbox, string calendarId, ODataQuery odata, PreferHeader prefer, HttpContext http, CalendarEventsIn useCase, CancellationToken cancellationToken)
    {
        var page = await useCase.ListAsync(mailbox.Ref, new CalendarId(calendarId), odata.Query, cancellationToken).ConfigureAwait(false);
        return GraphResults.Match(page, p =>
        {
            prefer.MarkTimeZoneApplied(http.Response);
            return GraphResults.Page(p, e => EventDtoMapper.ToDto(e, prefer), $"{Calendars(mailbox)}('{calendarId}')/events", odata.Query);
        });
    }

    private static async Task<IResult> CalendarViewAsync(
        Mailbox mailbox, string calendarId, ODataQuery odata, PreferHeader prefer, HttpContext http, CalendarEventsIn useCase, CancellationToken cancellationToken)
    {
        var start = EventEndpoints.ParseBound(http.Request.Query, EventEndpoints.StartDateTime);
        var end = EventEndpoints.ParseBound(http.Request.Query, EventEndpoints.EndDateTime);
        if (!start.IsSuccess || !end.IsSuccess)
        {
            return GraphResults.Error(start.Error ?? end.Error!);
        }

        var page = await useCase.ViewAsync(mailbox.Ref, new CalendarId(calendarId), start.Value, end.Value, odata.Query, cancellationToken).ConfigureAwait(false);
        return GraphResults.Match(page, p =>
        {
            prefer.MarkTimeZoneApplied(http.Response);
            return GraphResults.Page(p, e => EventDtoMapper.ToDto(e, prefer), $"{Calendars(mailbox)}('{calendarId}')/calendarView", odata.Query);
        });
    }

    private static async Task<IResult> InstancesAsync(
        Mailbox mailbox, string eventId, ODataQuery odata, PreferHeader prefer, HttpContext http, CalendarEventsIn useCase, CancellationToken cancellationToken)
    {
        var start = EventEndpoints.ParseBound(http.Request.Query, EventEndpoints.StartDateTime);
        var end = EventEndpoints.ParseBound(http.Request.Query, EventEndpoints.EndDateTime);
        if (!start.IsSuccess || !end.IsSuccess)
        {
            return GraphResults.Error(start.Error ?? end.Error!);
        }

        var page = await useCase.InstancesAsync(mailbox.Ref, new Nightware.Domain.Kernel.EventId(eventId), start.Value, end.Value, odata.Query, cancellationToken).ConfigureAwait(false);
        return GraphResults.Match(page, p =>
        {
            prefer.MarkTimeZoneApplied(http.Response);
            return GraphResults.Page(p, e => EventDtoMapper.ToDto(e, prefer), $"{mailbox.ContextSegment}/events('{eventId}')/instances", odata.Query);
        });
    }

    private static async Task<IResult> CalendarViewDeltaAsync(
        Mailbox mailbox, ODataQuery odata, PreferHeader prefer, HttpContext http, Graph.Json.DeltaLinkCodec links, SyncCalendarView useCase, CancellationToken cancellationToken)
    {
        var cursor = links.ReadCursor(http.Request.Query, odata.Query);
        if (!cursor.IsSuccess)
        {
            return GraphResults.Error(cursor.Error);
        }

        var c = cursor.Value;
        DateTimeOffset? start = null, end = null;
        if (c.Token is null)
        {
            var from = EventEndpoints.ParseBound(http.Request.Query, EventEndpoints.StartDateTime);
            var to = EventEndpoints.ParseBound(http.Request.Query, EventEndpoints.EndDateTime);
            if (!from.IsSuccess || !to.IsSuccess)
            {
                return GraphResults.Error(from.Error ?? to.Error!);
            }

            (start, end) = (from.Value, to.Value);
        }

        var page = await useCase.ExecuteAsync(mailbox.Ref, start, end, c.Token, cancellationToken).ConfigureAwait(false);
        return GraphResults.Match(page, p =>
        {
            prefer.MarkTimeZoneApplied(http.Response);
            return GraphResults.Delta(p, e => EventDtoMapper.ToDto(e, prefer), c, "event", "#microsoft.graph.event");
        });
    }

    private static async Task<IResult> CreateEventAsync(
        Mailbox mailbox, string calendarId, EventWriteDto? body, ODataQuery odata, PreferHeader prefer, HttpContext http, CalendarEventsIn useCase, CancellationToken cancellationToken)
    {
        var newEvent = EventRequestMapper.ToNewEvent(body);
        if (!newEvent.IsSuccess)
        {
            return GraphResults.Error(newEvent.Error);
        }

        var result = await useCase.CreateAsync(mailbox.Ref, new CalendarId(calendarId), newEvent.Value, cancellationToken).ConfigureAwait(false);
        return GraphResults.Match(result, e =>
        {
            prefer.MarkTimeZoneApplied(http.Response);
            return GraphResults.CreatedAt(EventDtoMapper.ToDto(e, prefer), $"{mailbox.ContextSegment}/events", $"{mailbox.ContextSegment}/events('{Uri.EscapeDataString(e.Id.Value)}')", odata.Query.Select);
        });
    }

    private static IResult ListGroupsAsync(Mailbox mailbox, ODataQuery odata) =>
        GraphResults.Collection([new CalendarGroupDto()], $"{mailbox.ContextSegment}/calendarGroups", odata.Query);

    private static async Task<IResult> ListGroupCalendarsAsync(
        Mailbox mailbox, string groupId, ODataQuery odata, ListGroupCalendars useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(await useCase.ExecuteAsync(mailbox.Ref, groupId, cancellationToken).ConfigureAwait(false),
            list => GraphResults.Collection(list.Select(CalendarDto.From), $"{mailbox.ContextSegment}/calendarGroups('{groupId}')/calendars", odata.Query));
}

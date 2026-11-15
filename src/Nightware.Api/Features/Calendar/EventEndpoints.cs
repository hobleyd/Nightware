using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Nightware.Api.Features.Calendar.Dto;
using Nightware.Api.Graph;
using Nightware.Api.Graph.OData;
using Nightware.Api.Graph.Routing;
using Nightware.Application.Calendar.Events;
using Nightware.Domain.Kernel;
using EventId = Nightware.Domain.Kernel.EventId;

namespace Nightware.Api.Features.Calendar;

/// <summary><c>calendarView</c> and <c>events</c> (read, create, update, delete).</summary>
internal static class EventEndpoints
{
    public const string StartDateTime = "startDateTime";
    public const string EndDateTime = "endDateTime";

    public static void Map(RouteGroupBuilder graph)
    {
        graph.MapMailboxGet("/calendarView", CalendarViewAsync);
        graph.MapMailboxGet("/events", ListAsync);
        graph.MapMailboxPost("/events", CreateAsync);
        graph.MapMailboxGet("/events/{eventId}", GetAsync);
        graph.MapMailboxPatch("/events/{eventId}", UpdateAsync);
        graph.MapMailboxDelete("/events/{eventId}", DeleteAsync);
    }

    private static async Task<IResult> CalendarViewAsync(
        Mailbox mailbox, ODataQuery odata, PreferHeader prefer, HttpContext http, GetCalendarView calendarView, CancellationToken cancellationToken)
    {
        var start = ParseBound(http.Request.Query, StartDateTime);
        var end = ParseBound(http.Request.Query, EndDateTime);
        if (!start.IsSuccess || !end.IsSuccess)
        {
            return GraphResults.Error(start.Error ?? end.Error!);
        }

        var page = await calendarView.ExecuteAsync(mailbox.Ref, start.Value, end.Value, odata.Query, cancellationToken).ConfigureAwait(false);
        return GraphResults.Match(page, p =>
        {
            prefer.MarkTimeZoneApplied(http.Response);
            return GraphResults.Page(p, e => EventDtoMapper.ToDto(e, prefer), $"{mailbox.ContextSegment}/calendarView", odata.Query);
        });
    }

    private static async Task<IResult> ListAsync(
        Mailbox mailbox, ODataQuery odata, PreferHeader prefer, HttpContext http, ListEvents events, CancellationToken cancellationToken)
    {
        var page = await events.ExecuteAsync(mailbox.Ref, odata.Query, cancellationToken).ConfigureAwait(false);
        return GraphResults.Match(page, p =>
        {
            prefer.MarkTimeZoneApplied(http.Response);
            return GraphResults.Page(p, e => EventDtoMapper.ToDto(e, prefer), $"{mailbox.ContextSegment}/events", odata.Query);
        });
    }

    private static async Task<IResult> GetAsync(
        Mailbox mailbox, string eventId, ODataQuery odata, PreferHeader prefer, HttpContext http, GetEvent getEvent, CancellationToken cancellationToken)
    {
        var result = await getEvent.ExecuteAsync(mailbox.Ref, new EventId(eventId), cancellationToken).ConfigureAwait(false);
        return Entity(result, mailbox, odata, prefer, http);
    }

    private static async Task<IResult> CreateAsync(
        Mailbox mailbox, EventWriteDto? body, ODataQuery odata, PreferHeader prefer, HttpContext http, CreateEvent create, CancellationToken cancellationToken)
    {
        var newEvent = EventRequestMapper.ToNewEvent(body);
        if (!newEvent.IsSuccess)
        {
            return GraphResults.Error(newEvent.Error);
        }

        var result = await create.ExecuteAsync(mailbox.Ref, newEvent.Value, cancellationToken).ConfigureAwait(false);
        return GraphResults.Match(result, e =>
        {
            prefer.MarkTimeZoneApplied(http.Response);
            return GraphResults.Created(EventDtoMapper.ToDto(e, prefer), $"{mailbox.ContextSegment}/events", odata.Query.Select);
        });
    }

    private static async Task<IResult> UpdateAsync(
        Mailbox mailbox, string eventId, EventWriteDto? body, ODataQuery odata, PreferHeader prefer, HttpContext http, UpdateEvent update, CancellationToken cancellationToken)
    {
        var patch = EventRequestMapper.ToPatch(body);
        if (!patch.IsSuccess)
        {
            return GraphResults.Error(patch.Error);
        }

        var result = await update.ExecuteAsync(mailbox.Ref, new EventId(eventId), patch.Value, cancellationToken).ConfigureAwait(false);
        return Entity(result, mailbox, odata, prefer, http);
    }

    private static async Task<IResult> DeleteAsync(Mailbox mailbox, string eventId, DeleteEvent delete, CancellationToken cancellationToken) =>
        GraphResults.Match(await delete.ExecuteAsync(mailbox.Ref, new EventId(eventId), cancellationToken).ConfigureAwait(false));

    private static IResult Entity(Result<Domain.Calendar.CalendarEvent> result, Mailbox mailbox, ODataQuery odata, PreferHeader prefer, HttpContext http) =>
        GraphResults.Match(result, e =>
        {
            prefer.MarkTimeZoneApplied(http.Response);
            return GraphResults.Entity(EventDtoMapper.ToDto(e, prefer), $"{mailbox.ContextSegment}/events", odata.Query.Select);
        });

    /// <summary>
    /// A <c>calendarView</c> bound. Graph requires both and reads an ISO 8601 value without an offset as UTC;
    /// NightMail sends <c>2026-06-01T00:00:00.000Z</c>.
    /// </summary>
    internal static Result<DateTimeOffset> ParseBound(IQueryCollection query, string name)
    {
        var raw = query[name].ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Error.Validation("This request requires a time window specified by the query string parameters startDateTime and endDateTime.", name);
        }

        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)
            ? value
            : Error.Validation($"'{raw}' is not a valid {name}.", name);
    }
}

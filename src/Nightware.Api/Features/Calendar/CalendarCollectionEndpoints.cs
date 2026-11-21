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

/// <summary>Graph <c>reminder</c>.</summary>
public sealed record ReminderDto
{
    public string? EventId { get; init; }

    public DateTimeTimeZoneDto? EventStartTime { get; init; }

    public DateTimeTimeZoneDto? EventEndTime { get; init; }

    public string? EventSubject { get; init; }

    public LocationDto? EventLocation { get; init; }

    public DateTimeTimeZoneDto? ReminderFireTime { get; init; }

    public static ReminderDto From(Reminder reminder, PreferHeader prefer)
    {
        ArgumentNullException.ThrowIfNull(reminder);
        return new ReminderDto
        {
            EventId = reminder.EventId.Value,
            EventStartTime = EventDtoMapper.ToDto(reminder.EventStartTime, false, prefer),
            EventEndTime = EventDtoMapper.ToDto(reminder.EventEndTime, false, prefer),
            EventSubject = reminder.EventSubject,
            EventLocation = new LocationDto { DisplayName = reminder.EventLocation?.DisplayName ?? string.Empty },
            ReminderFireTime = EventDtoMapper.ToDto(reminder.ReminderFireTime, false, prefer),
        };
    }
}

/// <summary>Graph <c>fileAttachment</c> on an event (request and response).</summary>
public sealed record EventAttachmentDto
{
    public const string FileAttachmentType = "#microsoft.graph.fileAttachment";

    [JsonPropertyName("@odata.type")]
    public string? ODataType { get; init; } = FileAttachmentType;

    public string? Id { get; init; }

    public DateTimeOffset? LastModifiedDateTime { get; init; }

    public string? Name { get; init; }

    public string? ContentType { get; init; }

    public long Size { get; init; }

    public bool IsInline { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContentBytes { get; init; }

    public static EventAttachmentDto From(EventAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        return new EventAttachmentDto
        {
            Id = attachment.Id.Value,
            LastModifiedDateTime = attachment.LastModifiedDateTime,
            Name = attachment.Name,
            ContentType = attachment.ContentType,
            Size = attachment.Size,
            ContentBytes = attachment.Content is { } content ? Convert.ToBase64String(content.Span) : null,
        };
    }
}

/// <summary>Graph <c>calendarPermission</c>.</summary>
public sealed record CalendarPermissionDto
{
    public string? Id { get; init; }

    public bool IsRemovable { get; init; } = true;

    public bool IsInsideOrganization { get; init; } = true;

    public string? Role { get; init; }

    public IReadOnlyList<string> AllowedRoles { get; init; } = [];

    public CalendarOwnerDto? EmailAddress { get; init; }

    public static CalendarPermissionDto From(CalendarShare share)
    {
        ArgumentNullException.ThrowIfNull(share);
        return new CalendarPermissionDto
        {
            Id = share.Id,
            Role = share.Role == CalendarRole.Write ? "write" : "read",
            EmailAddress = new CalendarOwnerDto { Name = share.EmailAddress.Name ?? share.EmailAddress.Address, Address = share.EmailAddress.Address },
        };
    }
}

/// <summary>Request body of <c>snoozeReminder</c>.</summary>
public sealed record SnoozeRequestDto
{
    public DateTimeTimeZoneDto? NewReminderTime { get; init; }
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
            scope.MapGet("/calendar/events", DefaultEventsAsync);
            scope.MapPost("/calendar/events", DefaultCreateEventAsync);
            scope.MapGet("/calendar/calendarView", DefaultCalendarViewAsync);
            scope.MapGet("/events/{eventId}/instances", InstancesAsync);
            scope.MapGet("/calendarView/delta", CalendarViewDeltaAsync);
            scope.MapGet("/reminderView(startDateTime={start},endDateTime={end})", ReminderViewAsync);
            scope.MapPost("/events/{eventId}/snoozeReminder", SnoozeAsync);
            scope.MapPost("/events/{eventId}/dismissReminder", DismissAsync);
            scope.MapGet("/events/{eventId}/attachments", ListAttachmentsAsync);
            scope.MapPost("/events/{eventId}/attachments", AddAttachmentAsync);
            scope.MapGet("/events/{eventId}/attachments/{attachmentId}", GetAttachmentAsync);
            scope.MapDelete("/events/{eventId}/attachments/{attachmentId}", DeleteAttachmentAsync);
            scope.MapGet("/calendars/{calendarId}/calendarPermissions", ListPermissionsAsync);
            scope.MapPost("/calendars/{calendarId}/calendarPermissions", CreatePermissionAsync);
            scope.MapPatch("/calendars/{calendarId}/calendarPermissions/{permissionId}", UpdatePermissionAsync);
            scope.MapDelete("/calendars/{calendarId}/calendarPermissions/{permissionId}", DeletePermissionAsync);
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

    private static Task<IResult> ListEventsAsync(
        Mailbox mailbox, string calendarId, ODataQuery odata, PreferHeader prefer, HttpContext http, CalendarEventsIn useCase, CancellationToken cancellationToken) =>
        ListEventsInAsync(mailbox, calendarId, $"{Calendars(mailbox)}('{calendarId}')", odata, prefer, http, useCase, cancellationToken);

    private static async Task<IResult> ListEventsInAsync(
        Mailbox mailbox, string calendarId, string context, ODataQuery odata, PreferHeader prefer, HttpContext http, CalendarEventsIn useCase, CancellationToken cancellationToken)
    {
        var page = await useCase.ListAsync(mailbox.Ref, new CalendarId(calendarId), odata.Query, cancellationToken).ConfigureAwait(false);
        return GraphResults.Match(page, p =>
        {
            prefer.MarkTimeZoneApplied(http.Response);
            return GraphResults.Page(p, e => EventDtoMapper.ToDto(e, prefer), $"{context}/events", odata.Query);
        });
    }

    /// <summary>
    /// <c>calendar/events</c>, <c>calendar/calendarView</c>: the default calendar's, through its own
    /// <c>calendars/{id}/…</c> handling, so they can't drift from it.
    /// </summary>
    private static async Task<IResult> DefaultEventsAsync(
        Mailbox mailbox, ODataQuery odata, PreferHeader prefer, HttpContext http, GetCalendar calendar, CalendarEventsIn useCase, CancellationToken cancellationToken)
    {
        var found = await calendar.ExecuteAsync(mailbox.Ref, null, cancellationToken).ConfigureAwait(false);
        return found.IsSuccess
            ? await ListEventsInAsync(mailbox, found.Value.Id.Value, $"{mailbox.ContextSegment}/calendar", odata, prefer, http, useCase, cancellationToken).ConfigureAwait(false)
            : GraphResults.Error(found.Error);
    }

    private static async Task<IResult> DefaultCalendarViewAsync(
        Mailbox mailbox, ODataQuery odata, PreferHeader prefer, HttpContext http, GetCalendar calendar, CalendarEventsIn useCase, CancellationToken cancellationToken)
    {
        var found = await calendar.ExecuteAsync(mailbox.Ref, null, cancellationToken).ConfigureAwait(false);
        return found.IsSuccess
            ? await CalendarViewInAsync(mailbox, found.Value.Id.Value, $"{mailbox.ContextSegment}/calendar", odata, prefer, http, useCase, cancellationToken).ConfigureAwait(false)
            : GraphResults.Error(found.Error);
    }

    private static async Task<IResult> DefaultCreateEventAsync(
        Mailbox mailbox, EventWriteDto? body, ODataQuery odata, PreferHeader prefer, HttpContext http, GetCalendar calendar, CalendarEventsIn useCase, CancellationToken cancellationToken)
    {
        var found = await calendar.ExecuteAsync(mailbox.Ref, null, cancellationToken).ConfigureAwait(false);
        return found.IsSuccess
            ? await CreateEventAsync(mailbox, found.Value.Id.Value, body, odata, prefer, http, useCase, cancellationToken).ConfigureAwait(false)
            : GraphResults.Error(found.Error);
    }

    private static Task<IResult> CalendarViewAsync(
        Mailbox mailbox, string calendarId, ODataQuery odata, PreferHeader prefer, HttpContext http, CalendarEventsIn useCase, CancellationToken cancellationToken) =>
        CalendarViewInAsync(mailbox, calendarId, $"{Calendars(mailbox)}('{calendarId}')", odata, prefer, http, useCase, cancellationToken);

    private static async Task<IResult> CalendarViewInAsync(
        Mailbox mailbox, string calendarId, string context, ODataQuery odata, PreferHeader prefer, HttpContext http, CalendarEventsIn useCase, CancellationToken cancellationToken)
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
            return GraphResults.Page(p, e => EventDtoMapper.ToDto(e, prefer), $"{context}/calendarView", odata.Query);
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

    private static async Task<IResult> ReminderViewAsync(
        Mailbox mailbox, string start, string end, PreferHeader prefer, HttpContext http, GetReminderView useCase, CancellationToken cancellationToken)
    {
        // The function's arguments arrive quoted: reminderView(startDateTime='…',endDateTime='…').
        var from = ParseLiteral(start, EventEndpoints.StartDateTime);
        var to = ParseLiteral(end, EventEndpoints.EndDateTime);
        if (!from.IsSuccess || !to.IsSuccess)
        {
            return GraphResults.Error(from.Error ?? to.Error!);
        }

        var reminders = await useCase.ExecuteAsync(mailbox.Ref, from.Value, to.Value, cancellationToken).ConfigureAwait(false);
        return GraphResults.Match(reminders, list =>
        {
            prefer.MarkTimeZoneApplied(http.Response);
            return GraphResults.Collection(list.Select(r => ReminderDto.From(r, prefer)), "Collection(microsoft.graph.reminder)", Application.Common.Query.ItemQuery.Default);
        });
    }

    private static Result<DateTimeOffset> ParseLiteral(string raw, string name) =>
        DateTimeOffset.TryParse(Uri.UnescapeDataString(raw).Trim('\''), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var value)
            ? value
            : Error.Validation($"'{raw}' is not a valid {name}.", name);

    private static async Task<IResult> SnoozeAsync(Mailbox mailbox, string eventId, [FromBody] SnoozeRequestDto? body, SnoozeReminder useCase, CancellationToken cancellationToken)
    {
        var time = body?.NewReminderTime is { } t ? new DateTimeTimeZone(t.DateTime, string.IsNullOrWhiteSpace(t.TimeZone) ? "UTC" : t.TimeZone) : null;
        var result = await useCase.ExecuteAsync(mailbox.Ref, new Nightware.Domain.Kernel.EventId(eventId), time, cancellationToken).ConfigureAwait(false);

        // Exchange accepts a snooze (202) and completes a dismissal (200), as captured.
        return result.IsSuccess ? Results.Accepted() : GraphResults.Error(result.Error);
    }

    private static async Task<IResult> DismissAsync(Mailbox mailbox, string eventId, DismissReminder useCase, CancellationToken cancellationToken)
    {
        var result = await useCase.ExecuteAsync(mailbox.Ref, new Nightware.Domain.Kernel.EventId(eventId), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? Results.Ok() : GraphResults.Error(result.Error);
    }

    private static string Attachments(Mailbox mailbox, string eventId) => $"{mailbox.ContextSegment}/events('{eventId}')/attachments";

    private static async Task<IResult> ListAttachmentsAsync(
        Mailbox mailbox, string eventId, ODataQuery odata, EventAttachmentsUseCase useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(await useCase.ListAsync(mailbox.Ref, new Nightware.Domain.Kernel.EventId(eventId), cancellationToken).ConfigureAwait(false),
            list => GraphResults.Collection(list.Select(EventAttachmentDto.From), Attachments(mailbox, eventId), odata.Query));

    private static async Task<IResult> GetAttachmentAsync(
        Mailbox mailbox, string eventId, string attachmentId, ODataQuery odata, EventAttachmentsUseCase useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(await useCase.GetAsync(mailbox.Ref, new Nightware.Domain.Kernel.EventId(eventId), new AttachmentId(attachmentId), cancellationToken).ConfigureAwait(false),
            a => GraphResults.Entity(EventAttachmentDto.From(a), Attachments(mailbox, eventId), odata.Query.Select));

    private static async Task<IResult> AddAttachmentAsync(
        Mailbox mailbox, string eventId, [FromBody] EventAttachmentDto? body, EventAttachmentsUseCase useCase, CancellationToken cancellationToken)
    {
        if (body?.ODataType is { } type && !string.Equals(type, EventAttachmentDto.FileAttachmentType, StringComparison.OrdinalIgnoreCase))
        {
            return GraphResults.Error(Error.Unsupported($"Only file attachments can be added to an event ('{type}').", "@odata.type"));
        }

        byte[] content;
        try
        {
            content = Convert.FromBase64String(body?.ContentBytes ?? string.Empty);
        }
        catch (FormatException)
        {
            return GraphResults.Error(Error.Validation("contentBytes is not base64.", "contentBytes"));
        }

        var added = await useCase.AddAsync(mailbox.Ref, new Nightware.Domain.Kernel.EventId(eventId), body?.Name, body?.ContentType, content, cancellationToken).ConfigureAwait(false);
        return GraphResults.Match(added, a => GraphResults.CreatedAt(EventAttachmentDto.From(a), Attachments(mailbox, eventId),
            $"{mailbox.ContextSegment}/events('{Uri.EscapeDataString(eventId)}')/Attachments('{Uri.EscapeDataString(a.Id.Value)}')"));
    }

    private static async Task<IResult> DeleteAttachmentAsync(
        Mailbox mailbox, string eventId, string attachmentId, EventAttachmentsUseCase useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(await useCase.DeleteAsync(mailbox.Ref, new Nightware.Domain.Kernel.EventId(eventId), new AttachmentId(attachmentId), cancellationToken).ConfigureAwait(false));

    private static string Permissions(Mailbox mailbox, string calendarId) => $"{Calendars(mailbox)}('{calendarId}')/calendarPermissions";

    private static async Task<IResult> ListPermissionsAsync(
        Mailbox mailbox, string calendarId, ODataQuery odata, CalendarPermissionsUseCase useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(await useCase.ListAsync(mailbox.Ref, new CalendarId(calendarId), cancellationToken).ConfigureAwait(false),
            list => GraphResults.Collection(list.Select(CalendarPermissionDto.From), Permissions(mailbox, calendarId), odata.Query));

    private static async Task<IResult> CreatePermissionAsync(
        Mailbox mailbox, string calendarId, [FromBody] CalendarPermissionDto? body, CalendarPermissionsUseCase useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(await useCase.CreateAsync(mailbox.Ref, new CalendarId(calendarId), body?.EmailAddress?.Address, body?.Role, cancellationToken).ConfigureAwait(false),
            p => GraphResults.CreatedAt(CalendarPermissionDto.From(p), Permissions(mailbox, calendarId),
                $"{mailbox.ContextSegment}/calendars('{Uri.EscapeDataString(calendarId)}')/calendarPermissions('{Uri.EscapeDataString(p.Id)}')"));

    private static async Task<IResult> UpdatePermissionAsync(
        Mailbox mailbox, string calendarId, string permissionId, ODataQuery odata, [FromBody] CalendarPermissionDto? body, CalendarPermissionsUseCase useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(await useCase.UpdateAsync(mailbox.Ref, new CalendarId(calendarId), permissionId, body?.Role, cancellationToken).ConfigureAwait(false),
            p => GraphResults.Entity(CalendarPermissionDto.From(p), Permissions(mailbox, calendarId), odata.Query.Select));

    private static async Task<IResult> DeletePermissionAsync(
        Mailbox mailbox, string calendarId, string permissionId, CalendarPermissionsUseCase useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(await useCase.DeleteAsync(mailbox.Ref, new CalendarId(calendarId), permissionId, cancellationToken).ConfigureAwait(false));

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

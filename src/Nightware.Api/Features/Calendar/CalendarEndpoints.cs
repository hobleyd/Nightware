using Microsoft.AspNetCore.Routing;

namespace Nightware.Api.Features.Calendar;

public static class CalendarEndpoints
{
    /// <summary>
    /// Maps the Calendar routes onto a Graph version group (<c>/v1.0</c> and <c>/beta</c>); see docs/api-plumbing.md.
    /// Every route is mailbox-scoped (<c>/me/…</c> and <c>/users/{user}/…</c>).
    /// </summary>
    public static RouteGroupBuilder MapCalendarEndpoints(this RouteGroupBuilder graph)
    {
        EventEndpoints.Map(graph);
        CalendarCollectionEndpoints.Map(graph);
        EventAttachmentUploadEndpoints.Map(graph);
        SchedulingEndpoints.Map(graph);
        MessageEventEndpoints.Map(graph);
        ScheduleEndpoints.Map(graph);
        return graph;
    }
}

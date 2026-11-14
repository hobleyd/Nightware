using Microsoft.AspNetCore.Routing;

namespace Nightware.Api.Features.Calendar;

public static class CalendarEndpoints
{
    /// <summary>
    /// Maps the Calendar routes onto a Graph version group (<c>/v1.0</c> and <c>/beta</c>). Owned by the phase
    /// that builds Calendar; see docs/api-plumbing.md.
    /// </summary>
    public static RouteGroupBuilder MapCalendarEndpoints(this RouteGroupBuilder graph) => graph;
}

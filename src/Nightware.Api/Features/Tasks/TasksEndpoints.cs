using Microsoft.AspNetCore.Routing;

namespace Nightware.Api.Features.Tasks;

public static class TasksEndpoints
{
    /// <summary>
    /// Maps the Tasks routes onto a Graph version group (<c>/v1.0</c> and <c>/beta</c>). Owned by the phase
    /// that builds Tasks; see docs/api-plumbing.md.
    /// </summary>
    public static RouteGroupBuilder MapTasksEndpoints(this RouteGroupBuilder graph) => graph;
}

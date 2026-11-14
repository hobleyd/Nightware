using Microsoft.AspNetCore.Routing;

namespace Nightware.Api.Features.People;

public static class PeopleEndpoints
{
    /// <summary>
    /// Maps the People routes onto a Graph version group (<c>/v1.0</c> and <c>/beta</c>). Owned by the phase
    /// that builds People; see docs/api-plumbing.md.
    /// </summary>
    public static RouteGroupBuilder MapPeopleEndpoints(this RouteGroupBuilder graph) => graph;
}

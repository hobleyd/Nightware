using Microsoft.AspNetCore.Routing;

namespace Nightware.Api.Features.Rooms;

public static class RoomsEndpoints
{
    /// <summary>
    /// Maps the Rooms routes onto a Graph version group (<c>/v1.0</c> and <c>/beta</c>). Owned by the phase
    /// that builds Rooms; see docs/api-plumbing.md.
    /// </summary>
    public static RouteGroupBuilder MapRoomsEndpoints(this RouteGroupBuilder graph) => graph;
}

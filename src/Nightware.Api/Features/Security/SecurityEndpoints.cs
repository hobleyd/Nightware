using Microsoft.AspNetCore.Routing;

namespace Nightware.Api.Features.Security;

public static class SecurityEndpoints
{
    /// <summary>
    /// Maps the Security routes onto a Graph version group (<c>/v1.0</c> and <c>/beta</c>). Owned by the phase
    /// that builds Security; see docs/api-plumbing.md.
    /// </summary>
    public static RouteGroupBuilder MapSecurityEndpoints(this RouteGroupBuilder graph) => graph;
}

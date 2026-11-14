using Microsoft.AspNetCore.Routing;

namespace Nightware.Api.Features.Directory;

public static class DirectoryEndpoints
{
    /// <summary>
    /// Maps the Directory routes onto a Graph version group (<c>/v1.0</c> and <c>/beta</c>). Owned by the phase
    /// that builds Directory; see docs/api-plumbing.md.
    /// </summary>
    public static RouteGroupBuilder MapDirectoryEndpoints(this RouteGroupBuilder graph) => graph;
}

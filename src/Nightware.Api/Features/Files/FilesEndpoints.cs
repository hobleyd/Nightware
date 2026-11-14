using Microsoft.AspNetCore.Routing;

namespace Nightware.Api.Features.Files;

public static class FilesEndpoints
{
    /// <summary>
    /// Maps the Files routes onto a Graph version group (<c>/v1.0</c> and <c>/beta</c>). Owned by the phase
    /// that builds Files; see docs/api-plumbing.md.
    /// </summary>
    public static RouteGroupBuilder MapFilesEndpoints(this RouteGroupBuilder graph) => graph;
}

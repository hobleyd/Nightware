using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Nightware.Api.Graph;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Features.Files;

public static class FilesEndpoints
{
    /// <summary>
    /// <c>/shares/{id}/driveItem[/content]</c> are deferred (ADR 0001 §43): NightMail only calls them for
    /// Microsoft accounts. The routes exist so the answer is a deliberate <c>501 notImplemented</c>
    /// rather than the unknown-route 400 the client would read as "not a Graph resource".
    /// </summary>
    public static RouteGroupBuilder MapFilesEndpoints(this RouteGroupBuilder graph)
    {
        graph.MapGet("/shares/{shareId}/driveItem", NotAvailable);
        graph.MapGet("/shares/{shareId}/driveItem/content", NotAvailable);
        return graph;
    }

    internal static IResult NotAvailable() =>
        GraphResults.Error(Error.Unsupported("Opening SharePoint/OneDrive sharing links is not available on NightMail Server."));
}

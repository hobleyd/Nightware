using Microsoft.AspNetCore.Routing;
using Nightware.Api.Features.Mail.Read;
using Nightware.Api.Features.Mail.Write;

namespace Nightware.Api.Features.Mail;

public static class MailEndpoints
{
    /// <summary>
    /// Maps the Mail routes onto a Graph version group (<c>/v1.0</c> and <c>/beta</c>). Read routes are owned by
    /// Phase 2 and write routes by Phase 3, each in its own folder; see docs/api-plumbing.md.
    /// </summary>
    public static RouteGroupBuilder MapMailEndpoints(this RouteGroupBuilder graph) =>
        graph.MapMailReadEndpoints().MapMailWriteEndpoints();
}

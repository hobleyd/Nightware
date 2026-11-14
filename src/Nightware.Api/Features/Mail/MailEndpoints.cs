using Microsoft.AspNetCore.Routing;

namespace Nightware.Api.Features.Mail;

public static class MailEndpoints
{
    /// <summary>
    /// Maps the Mail routes onto a Graph version group (<c>/v1.0</c> and <c>/beta</c>). Owned by the phase
    /// that builds Mail; see docs/api-plumbing.md.
    /// </summary>
    public static RouteGroupBuilder MapMailEndpoints(this RouteGroupBuilder graph) => graph;
}

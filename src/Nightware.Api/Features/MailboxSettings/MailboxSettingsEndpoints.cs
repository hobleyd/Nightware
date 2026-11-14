using Microsoft.AspNetCore.Routing;

namespace Nightware.Api.Features.MailboxSettings;

public static class MailboxSettingsEndpoints
{
    /// <summary>
    /// Maps the MailboxSettings routes onto a Graph version group (<c>/v1.0</c> and <c>/beta</c>). Owned by the phase
    /// that builds MailboxSettings; see docs/api-plumbing.md.
    /// </summary>
    public static RouteGroupBuilder MapMailboxSettingsEndpoints(this RouteGroupBuilder graph) => graph;
}

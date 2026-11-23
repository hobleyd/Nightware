using Microsoft.AspNetCore.Routing;

namespace Nightware.Api.Features.Presence;

/// <summary>Presence over Nextcloud user status (phase 25, ADR 0009).</summary>
public static class PresenceEndpoints
{
    public static RouteGroupBuilder MapPresenceEndpoints(this RouteGroupBuilder graph) => graph;
}

using Microsoft.AspNetCore.Routing;

namespace Nightware.Api.Features.Teams;

/// <summary>Teams and channels over Nextcloud Teams (circles) and Talk (phase 24, ADR 0009).</summary>
public static class TeamsEndpoints
{
    public static RouteGroupBuilder MapTeamsEndpoints(this RouteGroupBuilder graph) => graph;
}

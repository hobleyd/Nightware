using Microsoft.AspNetCore.Routing;

namespace Nightware.Api.Features.Calls;

/// <summary>Delegated calls that ring through Talk (phase 26, ADR 0009).</summary>
public static class CallsEndpoints
{
    public static RouteGroupBuilder MapCallsEndpoints(this RouteGroupBuilder graph) => graph;
}

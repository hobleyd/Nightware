using Microsoft.AspNetCore.Routing;

namespace Nightware.Api.Features.OnlineMeetings;

/// <summary>Online meetings over Talk rooms, with their recordings and transcripts (phase 26, ADR 0009).</summary>
public static class OnlineMeetingsEndpoints
{
    public static RouteGroupBuilder MapOnlineMeetingsEndpoints(this RouteGroupBuilder graph) => graph;
}

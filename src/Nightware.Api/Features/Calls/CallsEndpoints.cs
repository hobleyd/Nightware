using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

using Nightware.Api.Features.Calls.Dto;
using Nightware.Api.Graph;
using Nightware.Api.Graph.OData;
using Nightware.Application.Calls;

namespace Nightware.Api.Features.Calls;

/// <summary>
/// Delegated calls that ring through Talk (phase 26, ADR 0009 D7): <c>POST /communications/calls</c> with the caller's
/// own token (Graph asks for an application permission; there is no bot here), <c>GET</c> and <c>DELETE</c> by id.
/// </summary>
public static class CallsEndpoints
{
    private const string Calls = "/communications/calls";

    public static RouteGroupBuilder MapCallsEndpoints(this RouteGroupBuilder graph)
    {
        graph.MapPost(Calls, PlaceAsync);
        graph.MapGet(Calls + "/{callId}", GetAsync);
        graph.MapDelete(Calls + "/{callId}", HangUpAsync);
        return graph;
    }

    private static async Task<IResult> PlaceAsync(JsonObject? body, ODataQuery odata, PlaceCall useCase, CancellationToken cancellationToken)
    {
        var call = CallRequests.ReadNew(body);
        if (!call.IsSuccess)
        {
            return GraphResults.Error(call.Error);
        }

        return GraphResults.Match(
            await useCase.ExecuteAsync(call.Value, cancellationToken).ConfigureAwait(false),
            placed => GraphResults.CreatedAt(CallDto.From(placed), "communications/calls", $"communications/calls/{Uri.EscapeDataString(placed.Id)}", odata.Query.Select));
    }

    private static async Task<IResult> GetAsync(string callId, ODataQuery odata, GetCall useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(
            await useCase.ExecuteAsync(callId, cancellationToken).ConfigureAwait(false),
            call => GraphResults.Entity(CallDto.From(call), "communications/calls", odata.Query.Select));

    private static async Task<IResult> HangUpAsync(string callId, HangUpCall useCase, CancellationToken cancellationToken) =>
        GraphResults.Match(await useCase.ExecuteAsync(callId, cancellationToken).ConfigureAwait(false));
}

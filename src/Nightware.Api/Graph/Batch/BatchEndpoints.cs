using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Nightware.Api.Graph.Json;

namespace Nightware.Api.Graph.Batch;

/// <summary>JSON batching: <c>POST /$batch</c> under each version root (ADR 0006 D9, phase 11).</summary>
public static class BatchEndpoints
{
    public static IServiceCollection AddGraphBatch(this IServiceCollection services)
    {
        services.TryAddSingleton<GraphBatchPipeline>();
        services.TryAddSingleton<BatchSubRequest>();
        services.TryAddSingleton<BatchDispatcher>();
        return services;
    }

    public static RouteGroupBuilder MapBatchEndpoints(this RouteGroupBuilder graph)
    {
        graph.MapPost("/$batch", HandleAsync);
        return graph;
    }

    private static async Task<IResult> HandleAsync(HttpContext http, JsonObject body, BatchDispatcher dispatcher)
    {
        var items = BatchRequestReader.Read(body);
        if (!items.IsSuccess)
        {
            return GraphResults.Error(items.Error);
        }

        var responses = await dispatcher.DispatchAsync(http, items.Value).ConfigureAwait(false);
        return Results.Json(responses, GraphJson.Options);
    }
}

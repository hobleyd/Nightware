using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Http;

using Nightware.Api.Graph.Errors;

namespace Nightware.Api.Graph.Batch;

/// <summary>
/// Runs a validated batch (ADR 0006 D9). Requests without <c>dependsOn</c> run concurrently, at most
/// <see cref="MaxConcurrency"/> at a time; a request runs only after everything it depends on, and is
/// answered <c>424</c> without running when one of those failed. Responses come back in request order.
/// </summary>
public sealed class BatchDispatcher(BatchSubRequest subRequest)
{
    /// <summary>Exchange Online's per-mailbox concurrency limit, which Graph applies to batches too.</summary>
    public const int MaxConcurrency = 4;

    public const string FailedDependencyCode = "FailedDependency";

    public async Task<JsonObject> DispatchAsync(HttpContext batch, IReadOnlyList<BatchItem> items)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(items);

        using var gate = new SemaphoreSlim(MaxConcurrency);
        var byId = items.ToDictionary(static i => i.Id, StringComparer.OrdinalIgnoreCase);
        var running = new Dictionary<string, Task<BatchResponse>>(StringComparer.OrdinalIgnoreCase);

        // BatchRequestReader has ruled out cycles and unknown ids, so this recursion ends.
        Task<BatchResponse> Start(string id)
        {
            if (!running.TryGetValue(id, out var task))
            {
                task = RunAsync(byId[id], [.. byId[id].DependsOn.Select(Start)]);
                running[id] = task;
            }

            return task;
        }

        async Task<BatchResponse> RunAsync(BatchItem item, Task<BatchResponse>[] dependencies)
        {
            var settled = await Task.WhenAll(dependencies).ConfigureAwait(false);
            if (settled.Any(static d => d.Status >= StatusCodes.Status400BadRequest))
            {
                return FailedDependency(batch, item);
            }

            await gate.WaitAsync(batch.RequestAborted).ConfigureAwait(false);
            try
            {
                return await subRequest.RunAsync(batch, item).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }

        var tasks = items.Select(i => Start(i.Id)).ToList();
        var responses = await Task.WhenAll(tasks).ConfigureAwait(false);
        return new JsonObject { ["responses"] = new JsonArray([.. responses.Select(static r => (JsonNode)r.Json)]) };
    }

    private static BatchResponse FailedDependency(HttpContext batch, BatchItem item)
    {
        var descriptor = new GraphErrorDescriptor(
            StatusCodes.Status424FailedDependency,
            FailedDependencyCode,
            "Request failed as dependent request failed",
            null);
        // Graph's own wording, and no headers: the request never ran (captured, batch.post-depends-on).
        return new BatchResponse(descriptor.StatusCode, new JsonObject
        {
            ["id"] = item.Id,
            ["status"] = descriptor.StatusCode,
            ["body"] = GraphErrorWriter.Envelope(batch, descriptor),
        });
    }
}

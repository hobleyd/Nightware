using System.Text.Json.Nodes;

namespace Nightware.Adapters.Jmap.Core.Requests;

/// <summary>A wire call: one chunk of a logical call (<see cref="LogicalCallId"/>).</summary>
internal sealed record WireInvocation(string WireCallId, JmapInvocation Logical)
{
    public string LogicalCallId => Logical.CallId;
}

/// <summary>The HTTP requests that carry one logical request, in order.</summary>
internal sealed record JmapRequestPlan(IReadOnlyList<IReadOnlyList<WireInvocation>> Batches)
{
    /// <summary>Wire call ids of each logical call (more than one when a /get was chunked).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> WireIds { get; init; } = new Dictionary<string, IReadOnlyList<string>>();

    /// <summary>
    /// True when the request goes out as more than one HTTP request. The server then processes each
    /// batch on its own: state read in one is not read "together with" calls in the next, and a
    /// failure part-way leaves the earlier batches applied.
    /// </summary>
    public bool IsSplit => Batches.Count > 1;
}

/// <summary>
/// Fits a logical request to the session limits:
/// <list type="bullet">
/// <item>A <c>Foo/get</c> with an explicit <c>ids</c> list longer than <c>maxObjectsInGet</c> is
/// split into several calls whose responses are merged again — unless a later call
/// back-references it (the server would only see one chunk), in which case it is sent whole.</item>
/// <item>More calls than <c>maxCallsInRequest</c> are spread over sequential HTTP requests, cutting
/// only where no later call references an earlier batch. Created ids are carried across.</item>
/// </list>
/// A request that cannot be fitted is sent as-is and the server's limit error is mapped.
/// </summary>
internal static class JmapRequestPlanner
{
    public static JmapRequestPlan Plan(IReadOnlyList<JmapInvocation> invocations, int maxObjectsInGet, int maxCallsInRequest)
    {
        ArgumentNullException.ThrowIfNull(invocations);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxObjectsInGet, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCallsInRequest, 1);

        var referenced = invocations.SelectMany(i => i.ReferencedCallIds()).ToHashSet(StringComparer.Ordinal);
        var wire = new List<WireInvocation>();
        var wireIds = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var invocation in invocations)
        {
            var chunks = invocation.Kind == JmapMethodKind.Get && !referenced.Contains(invocation.CallId)
                ? Chunk(invocation, maxObjectsInGet)
                : [new WireInvocation(invocation.CallId, invocation)];
            wire.AddRange(chunks);
            wireIds[invocation.CallId] = [.. chunks.Select(c => c.WireCallId)];
        }

        return new JmapRequestPlan(Batch(wire, maxCallsInRequest)) { WireIds = wireIds };
    }

    private static List<WireInvocation> Chunk(JmapInvocation invocation, int max)
    {
        if (invocation.Arguments["ids"] is not JsonArray ids || ids.Count <= max)
        {
            return [new WireInvocation(invocation.CallId, invocation)];
        }

        var chunks = new List<WireInvocation>();
        for (var offset = 0; offset < ids.Count; offset += max)
        {
            var args = (JsonObject)invocation.Arguments.DeepClone();
            args["ids"] = new JsonArray([.. ids.Skip(offset).Take(max).Select(n => n?.DeepClone())]);
            var wireId = offset == 0 ? invocation.CallId : $"{invocation.CallId}.{offset / max}";
            chunks.Add(new WireInvocation(wireId, invocation with { Arguments = args }));
        }

        return chunks;
    }

    private static List<IReadOnlyList<WireInvocation>> Batch(List<WireInvocation> wire, int max)
    {
        // Earliest wire index each call depends on (through back-references), or its own index.
        var firstIndexOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < wire.Count; i++)
        {
            firstIndexOf.TryAdd(wire[i].LogicalCallId, i);
        }

        var earliestDependency = new int[wire.Count];
        for (var i = 0; i < wire.Count; i++)
        {
            earliestDependency[i] = wire[i].Logical.ReferencedCallIds()
                .Select(id => firstIndexOf.TryGetValue(id, out var at) ? at : i)
                .DefaultIfEmpty(i)
                .Min();
        }

        // A cut before index b is allowed when no call at or after b references a call before b.
        bool CanCutAt(int b)
        {
            for (var j = b; j < wire.Count; j++)
            {
                if (earliestDependency[j] < b)
                {
                    return false;
                }
            }

            return true;
        }

        var batches = new List<IReadOnlyList<WireInvocation>>();
        var start = 0;
        while (start < wire.Count)
        {
            var end = Math.Min(start + max, wire.Count);
            if (end < wire.Count)
            {
                var cut = Enumerable.Range(start + 1, end - start).Reverse().FirstOrDefault(CanCutAt, -1);
                if (cut < 0)
                {
                    cut = Enumerable.Range(end + 1, wire.Count - end).FirstOrDefault(CanCutAt, wire.Count);
                }

                end = cut;
            }

            batches.Add(wire.GetRange(start, end - start));
            start = end;
        }

        return batches;
    }
}

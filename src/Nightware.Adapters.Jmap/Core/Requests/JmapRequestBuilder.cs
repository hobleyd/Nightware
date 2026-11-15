using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nightware.Adapters.Jmap.Core.Requests;

/// <summary>One method call as it will be put on the wire.</summary>
internal sealed record JmapInvocation(string CallId, string Method, JmapMethodKind Kind, IReadOnlyList<string> Capabilities, JsonObject Arguments)
{
    /// <summary>Call ids this invocation back-references through <c>#arg</c> arguments.</summary>
    public IEnumerable<string> ReferencedCallIds()
    {
        foreach (var (key, value) in Arguments)
        {
            if (key.StartsWith('#') && value is JsonObject reference && reference["resultOf"] is JsonValue resultOf
                && resultOf.TryGetValue<string>(out var id))
            {
                yield return id;
            }
        }
    }
}

/// <summary>
/// Builds one logical JMAP request: method calls with generated call ids, typed arguments,
/// result references, and the <c>using</c> capabilities each method needs. The client may
/// split it into several HTTP requests (session limits) — see <see cref="JmapRequestPlanner"/>.
/// </summary>
/// <example>
/// <code>
/// var request = new JmapRequestBuilder();
/// var query = request.Add(JmapMethods.EmailQuery, new EmailQueryRequest(accountId) { Filter = …, Limit = 50 });
/// var get = request.Add(JmapMethods.EmailGet, new EmailGetRequest(accountId) { Properties = … }, query.Ref("ids", JmapPaths.Ids));
/// var response = await jmap.SendAsync(request, ct);
/// var emails = response.Bind(r => r.Get(get));
/// </code>
/// </example>
internal sealed class JmapRequestBuilder
{
    private readonly List<JmapInvocation> _invocations = [];

    public IReadOnlyList<JmapInvocation> Invocations => _invocations;

    public int Count => _invocations.Count;

    /// <summary>Set by <see cref="RequireSingleRequest"/>.</summary>
    public bool RequiresSingleRequest { get; private set; }

    /// <summary>
    /// Marks a request whose correctness depends on its calls reaching the server together — a state
    /// read alongside the query it anchors, or a create and the submission that sends it. If the
    /// session's limits would split it over several HTTP requests, the client sends nothing and fails
    /// with <see cref="Domain.Kernel.ErrorKind.BackendUnavailable"/> instead of silently losing that.
    /// </summary>
    public JmapRequestBuilder RequireSingleRequest()
    {
        RequiresSingleRequest = true;
        return this;
    }

    /// <summary>
    /// Adds a call. Each <paramref name="references"/> entry replaces argument <c>x</c> with
    /// <c>#x</c> pointing at an earlier call's result (leave <c>x</c> null in <paramref name="arguments"/>).
    /// </summary>
    public JmapCall<TResponse> Add<TArgs, TResponse>(JmapMethod<TArgs, TResponse> method, TArgs arguments, params ReadOnlySpan<ArgumentReference> references)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(arguments);

        var json = JsonSerializer.SerializeToNode(arguments, method.ArgumentsType) as JsonObject
            ?? throw new ArgumentException("Method arguments must serialise to a JSON object.", nameof(arguments));

        foreach (var reference in references)
        {
            if (!_invocations.Any(i => i.CallId == reference.Reference.ResultOf && i.Method == reference.Reference.Name))
            {
                throw new ArgumentException($"Reference to unknown earlier call '{reference.Reference.ResultOf}' ({reference.Reference.Name}).", nameof(references));
            }

            json.Remove(reference.Argument);
            json["#" + reference.Argument] = reference.Reference.ToJson();
        }

        var callId = $"c{_invocations.Count}";
        _invocations.Add(new JmapInvocation(callId, method.Name, method.Kind, method.Capabilities, json));
        return new JmapCall<TResponse>(callId, method.Name, method.ResponseType);
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Nightware.Adapters.Jmap.Core.Errors;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Core.Requests;

/// <summary>One entry of <c>methodResponses</c>.</summary>
internal sealed record MethodResponse(string Name, JsonElement Arguments, string CallId);

/// <summary>
/// The responses to a <see cref="JmapRequestBuilder"/>'s calls. Read each call's typed
/// response with <see cref="Get{T}"/>; a method-level <c>error</c> comes back as a mapped
/// <see cref="Error"/>. Chunked <c>Foo/get</c> calls are merged transparently.
/// </summary>
internal sealed class JmapResponse
{
    private readonly IReadOnlyList<MethodResponse> _responses;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _wireIds;

    public JmapResponse(
        IReadOnlyList<MethodResponse> responses,
        string sessionState,
        IReadOnlyDictionary<string, string> createdIds,
        IReadOnlyDictionary<string, IReadOnlyList<string>> wireIds)
    {
        _responses = responses;
        SessionState = sessionState;
        CreatedIds = createdIds;
        _wireIds = wireIds;
    }

    public string SessionState { get; }

    /// <summary>Creation id → server id for every object created in this request.</summary>
    public IReadOnlyDictionary<string, string> CreatedIds { get; }

    public IReadOnlyList<MethodResponse> MethodResponses => _responses;

    /// <summary>The typed response of <paramref name="call"/>, or its mapped method error.</summary>
    public Result<T> Get<T>(JmapCall<T> call)
    {
        ArgumentNullException.ThrowIfNull(call);
        var wireIds = _wireIds.TryGetValue(call.CallId, out var ids) ? ids : [call.CallId];

        var parts = new List<JsonElement>(wireIds.Count);
        foreach (var wireId in wireIds)
        {
            var response = _responses.FirstOrDefault(r => r.CallId == wireId && (r.Name == call.Method || r.Name == "error"));
            if (response is null)
            {
                return JmapErrors.Unexpected($"The mail server sent no response for {call.Method} ({wireId}).");
            }

            if (response.Name == "error")
            {
                return MethodError(response, call.Method);
            }

            parts.Add(response.Arguments);
        }

        return parts.Count == 1
            ? Deserialize(parts[0], call)
            : Deserialize(Merge(parts), call);
    }

    /// <summary>
    /// An additional response sent under <paramref name="call"/>'s id, e.g. the implicit
    /// <c>Email/set</c> after <c>EmailSubmission/set</c> with <c>onSuccessUpdateEmail</c>.
    /// </summary>
    public Result<TResponse> GetAdditional<TArgs, TResponse>(JmapCall call, JmapMethod<TArgs, TResponse> method)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(method);
        var response = _responses.FirstOrDefault(r => r.CallId == call.CallId && r.Name == method.Name);
        if (response is null)
        {
            var error = _responses.FirstOrDefault(r => r.CallId == call.CallId && r.Name == "error");
            return error is null
                ? JmapErrors.Unexpected($"The mail server sent no {method.Name} response for {call.Method}.")
                : MethodError(error, call.Method);
        }

        return Deserialize(response.Arguments, method.ResponseType, method.Name);
    }

    private static Error MethodError(MethodResponse response, string method)
    {
        var error = response.Arguments.ValueKind == JsonValueKind.Object
            ? response.Arguments.Deserialize(JmapJsonContext.Default.MethodError) ?? new MethodError { Type = "serverFail" }
            : new MethodError { Type = "serverFail" };
        return JmapErrors.FromMethodError(error, method);
    }

    private static JsonElement Merge(List<JsonElement> parts)
    {
        var merged = JsonObject.Create(parts[0])!;
        var list = ArrayMember(merged, "list");
        var notFound = ArrayMember(merged, "notFound");
        foreach (var part in parts.Skip(1))
        {
            var next = JsonObject.Create(part)!;
            foreach (var item in next["list"] as JsonArray ?? [])
            {
                list.Add(item?.DeepClone());
            }

            foreach (var item in next["notFound"] as JsonArray ?? [])
            {
                notFound.Add(item?.DeepClone());
            }
        }

        return JsonElement.Parse(merged.ToJsonString());
    }

    private static JsonArray ArrayMember(JsonObject owner, string name)
    {
        if (owner[name] is JsonArray existing)
        {
            return existing;
        }

        var created = new JsonArray();
        owner[name] = created;
        return created;
    }

    private static Result<T> Deserialize<T>(JsonElement element, JmapCall<T> call) =>
        Deserialize(element, call.ResponseType, call.Method);

    private static Result<T> Deserialize<T>(JsonElement element, JsonTypeInfo<T> type, string method)
    {
        try
        {
            var value = element.Deserialize(type);
            return value is null ? JmapErrors.Unexpected($"{method}: empty response.") : value;
        }
        catch (JsonException ex)
        {
            return JmapErrors.Unexpected($"{method}: unreadable response ({ex.Message}).");
        }
    }
}

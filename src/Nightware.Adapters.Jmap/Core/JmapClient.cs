using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Nightware.Adapters.Jmap.Core.Auth;
using Nightware.Adapters.Jmap.Core.Errors;
using Nightware.Adapters.Jmap.Core.Http;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Core.Requests;
using Nightware.Adapters.Jmap.Core.Session;
using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Core;

/// <summary>
/// The JMAP API for feature ports: resolve the account behind a mailbox, then send a
/// <see cref="JmapRequestBuilder"/>. Scoped; credentials come from <see cref="IBackendCredentials"/>.
/// </summary>
internal interface IJmapClient
{
    /// <summary>The caller's JMAP session (cached per credential).</summary>
    Task<Result<JmapSession>> GetSessionAsync(CancellationToken cancellationToken);

    /// <summary>The account holding <paramref name="mailbox"/> for <paramref name="capability"/> (see <see cref="JmapAccountResolver"/>).</summary>
    Task<Result<JmapAccount>> ResolveAccountAsync(MailboxRef mailbox, CancellationToken cancellationToken, string capability = JmapCapabilities.Mail);

    /// <summary>
    /// Sends the request (split over several HTTP requests when session limits require it, unless
    /// <see cref="JmapRequestBuilder.RequireSingleRequest"/> forbids that: then nothing is sent and the
    /// result is <see cref="ErrorKind.BackendUnavailable"/>).
    /// Transport/HTTP/request-level failures are returned here; method-level errors are
    /// returned per call by <see cref="JmapResponse.Get{T}"/>.
    /// </summary>
    Task<Result<JmapResponse>> SendAsync(JmapRequestBuilder request, CancellationToken cancellationToken);
}

/// <inheritdoc />
internal sealed partial class JmapClient(
    JmapConnectionProvider connections,
    IHttpClientFactory httpClientFactory,
    IUserContext userContext,
    TimeProvider time,
    ILogger<JmapClient> logger) : IJmapClient
{
    public async Task<Result<JmapSession>> GetSessionAsync(CancellationToken cancellationToken)
    {
        var connection = await connections.ConnectAsync(cancellationToken).ConfigureAwait(false);
        return connection.Map(c => c.Session);
    }

    public async Task<Result<JmapAccount>> ResolveAccountAsync(MailboxRef mailbox, CancellationToken cancellationToken, string capability = JmapCapabilities.Mail)
    {
        var session = await GetSessionAsync(cancellationToken).ConfigureAwait(false);
        return session.Bind(s => JmapAccountResolver.Resolve(s, mailbox, userContext.Principal?.Mail, capability));
    }

    public async Task<Result<JmapResponse>> SendAsync(JmapRequestBuilder request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Count == 0)
        {
            throw new ArgumentException("A JMAP request needs at least one method call.", nameof(request));
        }

        var connected = await connections.ConnectAsync(cancellationToken).ConfigureAwait(false);
        if (!connected.IsSuccess)
        {
            return connected.Error;
        }

        var connection = connected.Value;
        var limits = connection.Session.Limits;
        var plan = JmapRequestPlanner.Plan(request.Invocations, limits.MaxObjectsInGet, limits.MaxCallsInRequest);
        if (plan.IsSplit && request.RequiresSingleRequest)
        {
            var methods = string.Join(", ", request.Invocations.Select(i => i.Method));
            LogWouldSplit(logger, methods, plan.Batches.Count, limits.MaxCallsInRequest, limits.MaxObjectsInGet);
            return Error.BackendUnavailable(
                "The mail server's request limits are too small for this operation to be carried out in one request.");
        }

        var client = httpClientFactory.CreateClient(JmapHttpClients.Api);

        var responses = new List<MethodResponse>();
        var createdIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var sessionState = connection.Session.State;

        foreach (var batch in plan.Batches)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, connection.Session.ApiUrl)
            {
                Content = new ByteArrayContent(Serialize(batch, createdIds)),
            };
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            message.Headers.Accept.ParseAdd("application/json");
            message.WithCredential(connection.Credential).MarkSafe(batch.All(c => c.Logical.Kind.IsSafe()));

            var sent = await JmapHttp.SendAsync(client, message, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            if (!sent.IsSuccess)
            {
                return sent.Error;
            }

            using var response = sent.Value;
            if (response.StatusCode != HttpStatusCode.OK)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    connections.Reject(connection.Credential);
                }

                return await JmapHttp.ReadFailureAsync(response, time, "api", connection.Credential.Strategy, cancellationToken).ConfigureAwait(false);
            }

            var parsed = await ParseAsync(response, cancellationToken).ConfigureAwait(false);
            if (!parsed.IsSuccess)
            {
                return parsed.Error;
            }

            responses.AddRange(parsed.Value.Responses);
            foreach (var (creationId, id) in parsed.Value.CreatedIds)
            {
                createdIds[creationId] = id;
            }

            sessionState = parsed.Value.SessionState ?? sessionState;
            connections.ObserveSessionState(connection, parsed.Value.SessionState);
        }

        return new JmapResponse(responses, sessionState, createdIds, plan.WireIds);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "A JMAP request that must reach the server in one piece ({Methods}) would be split into {Batches} HTTP requests "
        + "by the session limits (maxCallsInRequest {MaxCalls}, maxObjectsInGet {MaxObjects}); nothing was sent. Raise the mail server's limits.")]
    private static partial void LogWouldSplit(ILogger logger, string methods, int batches, int maxCalls, int maxObjects);

    private static byte[] Serialize(IReadOnlyList<WireInvocation> batch, Dictionary<string, string> createdIds)
    {
        var capabilities = new SortedSet<string>(StringComparer.Ordinal) { JmapCapabilities.Core };
        var calls = new JsonArray();
        foreach (var call in batch)
        {
            capabilities.UnionWith(call.Logical.Capabilities);
            calls.Add(new JsonArray(call.Logical.Method, call.Logical.Arguments.DeepClone(), call.WireCallId));
        }

        var body = new JsonObject
        {
            ["using"] = new JsonArray([.. capabilities.Select(c => (JsonNode?)c)]),
            ["methodCalls"] = calls,
        };
        if (createdIds.Count > 0)
        {
            var created = new JsonObject();
            foreach (var (creationId, id) in createdIds)
            {
                created[creationId] = id;
            }

            body["createdIds"] = created;
        }

        return Encoding.UTF8.GetBytes(body.ToJsonString());
    }

    private sealed record ParsedResponse(IReadOnlyList<MethodResponse> Responses, string? SessionState, IReadOnlyDictionary<string, string> CreatedIds);

    private static async Task<Result<ParsedResponse>> ParseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            var root = JsonElement.Parse(bytes);
            var responses = new List<MethodResponse>();
            foreach (var invocation in root.GetProperty("methodResponses").EnumerateArray())
            {
                if (invocation.ValueKind != JsonValueKind.Array || invocation.GetArrayLength() != 3)
                {
                    throw new JsonException("A method response must be [name, arguments, callId].");
                }

                responses.Add(new MethodResponse(invocation[0].GetString()!, invocation[1], invocation[2].GetString()!));
            }

            var created = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("createdIds", out var createdIds) && createdIds.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in createdIds.EnumerateObject())
                {
                    created[property.Name] = property.Value.GetString()!;
                }
            }

            var sessionState = root.TryGetProperty("sessionState", out var state) && state.ValueKind == JsonValueKind.String ? state.GetString() : null;
            return new ParsedResponse(responses, sessionState, created);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return JmapErrors.Unexpected($"The mail server's JMAP response is unreadable ({ex.Message}).");
        }
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Nightware.Adapters.Jmap.Core;
using Nightware.Adapters.Jmap.Core.Auth;
using Nightware.Adapters.Jmap.Core.Http;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Core.Requests;
using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Administration;

/// <summary>Sends management (<c>x:</c>) method calls to Stalwart as the console's own principal.</summary>
internal interface IStalwartManagementClient
{
    /// <summary>
    /// Sends every call in one HTTP request. Transport, HTTP and request-level failures are returned
    /// here; method errors per call through <see cref="JmapResponse.Get{T}"/>.
    /// </summary>
    Task<Result<JmapResponse>> SendAsync(JmapRequestBuilder request, CancellationToken cancellationToken);
}

/// <summary>
/// Typed <see cref="HttpClient"/> for Stalwart's management API. It has nothing to do with the
/// per-user JMAP pipeline: no session fetch, no <c>IBackendCredentials</c>, no
/// <c>JmapAuthenticationHandler</c>; the credential is the console's own (ADR 0002 D8), applied here.
/// A request made only of <c>/get</c> and <c>/query</c> calls is marked safe and may be retried.
/// </summary>
internal sealed partial class StalwartManagementClient(
    HttpClient http,
    IOptions<StalwartManagementOptions> options,
    TimeProvider time,
    ILogger<StalwartManagementClient> logger) : IStalwartManagementClient
{
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> NoChunking = new Dictionary<string, IReadOnlyList<string>>();

    public async Task<Result<JmapResponse>> SendAsync(JmapRequestBuilder request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Count == 0)
        {
            throw new ArgumentException("A request needs at least one method call.", nameof(request));
        }

        StalwartManagementOptions settings = options.Value;
        string methods = string.Join(", ", request.Invocations.Select(static i => i.Method));
        using var message = new HttpRequestMessage(HttpMethod.Post, settings.ApiUrl)
        {
            Content = new ByteArrayContent(Serialize(request)),
        };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        message.Headers.Accept.ParseAdd("application/json");
        message.Headers.Authorization = Authorization(settings);
        message.MarkSafe(request.Invocations.All(static i => i.Kind.IsSafe()));

        Result<HttpResponseMessage> sent = await JmapHttp.SendAsync(http, message, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        if (!sent.IsSuccess)
        {
            LogUnreachable(logger, methods, sent.Error.Message);
            return sent.Error;
        }

        using HttpResponseMessage response = sent.Value;
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            // The console's own credential: signing the admin in again would not help (unlike a user token).
            LogCredentialRejected(logger, methods);
            return Error.BackendUnavailable("The mail server rejected the admin console's management credential (StalwartManagement); check its configuration.");
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            // Never NotFound: callers read that as "no such account/domain" and would offer to create it.
            LogFailed(logger, methods, 404, ErrorKind.BackendUnavailable);
            return Error.BackendUnavailable("The mail server has no management API at StalwartManagement:ApiUrl (HTTP 404); check its configuration.");
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            Error error = await JmapHttp.ReadFailureAsync(response, time, "management API", CredentialStrategy.ServiceAccount, cancellationToken).ConfigureAwait(false);
            LogFailed(logger, methods, (int)response.StatusCode, error.Kind);
            return error;
        }

        Result<JmapClient.ParsedResponse> parsed = await JmapClient.ParseAsync(response, cancellationToken).ConfigureAwait(false);
        if (!parsed.IsSuccess)
        {
            LogFailed(logger, methods, (int)response.StatusCode, parsed.Error.Kind);
            return parsed.Error;
        }

        return new JmapResponse(parsed.Value.Responses, parsed.Value.SessionState ?? string.Empty, parsed.Value.CreatedIds, NoChunking);
    }

    private static AuthenticationHeaderValue Authorization(StalwartManagementOptions settings) =>
        string.IsNullOrEmpty(settings.ApiKey)
            ? new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.Username}:{settings.Password}")))
            : new AuthenticationHeaderValue("Bearer", settings.ApiKey);

    private static byte[] Serialize(JmapRequestBuilder request)
    {
        var calls = new JsonArray();
        foreach (JmapInvocation call in request.Invocations)
        {
            calls.Add(new JsonArray(call.Method, call.Arguments.DeepClone(), call.CallId));
        }

        var body = new JsonObject
        {
            ["using"] = new JsonArray(JmapCapabilities.Core, StalwartMethods.Capability),
            ["methodCalls"] = calls,
        };
        return Encoding.UTF8.GetBytes(body.ToJsonString());
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Stalwart rejected the admin console's management credential ({Methods}); check StalwartManagement")]
    private static partial void LogCredentialRejected(ILogger logger, string methods);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stalwart management call {Methods} failed: HTTP {Status} → {Kind}")]
    private static partial void LogFailed(ILogger logger, string methods, int status, ErrorKind kind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stalwart management API unreachable ({Methods}): {Reason}")]
    private static partial void LogUnreachable(ILogger logger, string methods, string reason);
}
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Cloudflare.Administration;

/// <summary>A successful Cloudflare v4 response: its <c>result</c> and, for a listing, how many pages there are.</summary>
internal sealed record CloudflareResponse(JsonNode? Result, int TotalPages);

/// <summary>Sends one call to Cloudflare's v4 API with an entity's token.</summary>
internal interface ICloudflareClient
{
    /// <param name="path">Relative to <see cref="CloudflareOptions.ApiBaseUrl"/>, with its query string.</param>
    Task<Result<CloudflareResponse>> SendAsync(HttpMethod method, string path, DnsProviderToken token, JsonObject? body, CancellationToken cancellationToken);
}

/// <summary>
/// Typed <see cref="HttpClient"/> for Cloudflare's v4 API. The token is the entity's, set per request as
/// <c>Authorization: Bearer</c>; it is never logged or put in an error. Failures become <see cref="Error"/>s: a
/// refused token is Validation (target <c>dnsToken</c>) so the admin can fix it, Cloudflare's own refusal of a
/// record is Validation with its message, throttling and outages are BackendUnavailable.
/// </summary>
internal sealed partial class CloudflareClient(HttpClient http, IOptions<CloudflareOptions> options, ILogger<CloudflareClient> logger) : ICloudflareClient
{
    /// <summary>Cloudflare error codes for a token that is invalid, expired or lacks the permission.</summary>
    private static readonly HashSet<int> TokenRefusals = [1000, 6003, 6111, 9103, 9106, 9109, 10000];

    public async Task<Result<CloudflareResponse>> SendAsync(HttpMethod method, string path, DnsProviderToken token, JsonObject? body, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(token);

        using var request = new HttpRequestMessage(method, new Uri(options.Value.ApiBaseUrl, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        request.Headers.Accept.ParseAdd("application/json");
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        string operation = $"{method} {Operation(path)}";
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException)
        {
            LogUnreachable(logger, operation, "TLS: " + ex.InnerException.Message);
            return Error.BackendUnavailable("The console does not trust Cloudflare's certificate. Point NIGHTWARE_SSL_CERT_FILE at the system CA bundle (see docs/operations/configuration.md).");
        }
        catch (Exception ex) when (ex is HttpRequestException or Polly.Timeout.TimeoutRejectedException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            LogUnreachable(logger, operation, ex.Message);
            return Error.BackendUnavailable("Cloudflare could not be reached. Try again shortly.");
        }

        using (response)
        {
            JsonObject? envelope = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode && envelope?["success"]?.GetValueKind() == JsonValueKind.True)
            {
                int pages = envelope["result_info"]?["total_pages"] is JsonValue p && p.TryGetValue(out int n) ? n : 1;
                return new CloudflareResponse(envelope["result"], pages);
            }

            Error error = Failure(response, envelope);
            LogFailed(logger, operation, (int)response.StatusCode, error.Kind, Codes(envelope));
            return error;
        }
    }

    private static Error Failure(HttpResponseMessage response, JsonObject? envelope)
    {
        List<(int Code, string Message)> errors = Errors(envelope);
        string said = errors.Count == 0 ? $"HTTP {(int)response.StatusCode}" : string.Join("; ", errors.Select(static e => e.Message));
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || errors.Any(static e => TokenRefusals.Contains(e.Code)))
        {
            return Error.Validation($"Cloudflare refused the token ({said}). Check that it is current and has the Zone / DNS / Edit permission on the domain's zone.", "dnsToken");
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            int? retryAfter = response.Headers.RetryAfter?.Delta is { } delta ? (int)Math.Ceiling(delta.TotalSeconds) : null;
            return Error.BackendUnavailable("Cloudflare is limiting requests. Try again in a few minutes.", retryAfter);
        }

        if ((int)response.StatusCode >= 500)
        {
            return Error.BackendUnavailable($"Cloudflare failed ({said}). Try again shortly.");
        }

        return response.StatusCode == HttpStatusCode.NotFound
            ? Error.NotFound($"Cloudflare: {said}.")
            : Error.Validation($"Cloudflare: {said}.");
    }

    private static async Task<JsonObject?> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            string text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return text.Length == 0 ? null : JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static List<(int Code, string Message)> Errors(JsonObject? envelope) =>
        envelope?["errors"] is JsonArray errors
            ? [.. errors.OfType<JsonObject>().Select(static e => (
                e["code"] is JsonValue c && c.TryGetValue(out int code) ? code : 0,
                e["message"] is JsonValue m && m.TryGetValue(out string? message) ? $"{message}" : "error"))]
            : [];

    private static string Codes(JsonObject? envelope) =>
        string.Join(',', Errors(envelope).Select(static e => e.Code.ToString(CultureInfo.InvariantCulture)));

    /// <summary>The path without its query string or ids, for logs: <c>zones/{id}/dns_records</c>.</summary>
    private static string Operation(string path)
    {
        string[] segments = path.Split('?')[0].Split('/');
        for (int i = 0; i < segments.Length; i++)
        {
            if (i > 0 && segments[i - 1] is "zones" or "dns_records")
            {
                segments[i] = "{id}";
            }
        }

        return string.Join('/', segments);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cloudflare call {Operation} failed: HTTP {Status} → {Kind} (codes {Codes})")]
    private static partial void LogFailed(ILogger logger, string operation, int status, ErrorKind kind, string codes);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cloudflare unreachable ({Operation}): {Reason}")]
    private static partial void LogUnreachable(ILogger logger, string operation, string reason);
}

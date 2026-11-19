using System.Net;
using System.Security.Authentication;

using Microsoft.Extensions.Logging;

using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.DnsHosts.Administration;

/// <summary>A DNS host's successful (2xx) answer.</summary>
internal sealed record DnsHostResponse(HttpStatusCode Status, string Body, IReadOnlyDictionary<string, string> Headers)
{
    public string? Header(string name) => Headers.TryGetValue(name, out string? value) ? value : null;
}

/// <summary>What a host said when it refused a call, read from its own error format.</summary>
/// <param name="TokenRefused">The credential is wrong, expired or lacks a permission (besides any 401/403).</param>
/// <param name="Throttled">The host is limiting requests (besides any 429).</param>
/// <param name="NotFound">What was asked for is not there, as far as the credential may know (whatever the status).</param>
internal sealed record DnsHostFault(string? Message, bool TokenRefused = false, bool Throttled = false, bool NotFound = false);

/// <summary>Sends one call to a DNS host's API; the caller has set the entity's credential on the request.</summary>
internal interface IDnsHostClient
{
    /// <param name="fault">Reads the host's own error format from a failed response's body.</param>
    Task<Result<DnsHostResponse>> SendAsync(DnsProvider provider, HttpRequestMessage request, Func<string, DnsHostFault> fault, CancellationToken cancellationToken);
}

/// <summary>
/// The one typed <see cref="HttpClient"/> for the DNS hosts in this adapter. The credential is never logged or put
/// in an error. Failures become <see cref="Error"/>s: a refused credential is Validation (target <c>dnsToken</c>) so
/// the admin can fix it, the host's refusal of a record is Validation with its message, a missing zone or record
/// NotFound, throttling and outages BackendUnavailable.
/// </summary>
internal sealed partial class DnsHostClient(HttpClient http, ILogger<DnsHostClient> logger) : IDnsHostClient
{
    public async Task<Result<DnsHostResponse>> SendAsync(DnsProvider provider, HttpRequestMessage request, Func<string, DnsHostFault> fault, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(fault);
        request.Headers.Accept.ParseAdd("application/json");

        string operation = $"{request.Method} {request.RequestUri?.AbsolutePath}";
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException)
        {
            LogUnreachable(logger, provider, operation, "TLS: " + ex.InnerException.Message);
            return Error.BackendUnavailable($"The console does not trust {provider.Name()}'s certificate. Point NIGHTWARE_SSL_CERT_FILE at the system CA bundle (see docs/operations/configuration.md).");
        }
        catch (Exception ex) when (ex is HttpRequestException or Polly.Timeout.TimeoutRejectedException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            LogUnreachable(logger, provider, operation, ex.Message);
            return Error.BackendUnavailable($"{provider.Name()} could not be reached. Try again shortly.");
        }

        using (response)
        {
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers.Concat(response.Content.Headers))
                {
                    headers[header.Key] = string.Join(',', header.Value);
                }

                return new DnsHostResponse(response.StatusCode, body, headers);
            }

            DnsHostFault said = Read(fault, body);
            TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
            Error error = Failure(provider, response.StatusCode, said, retryAfter);
            LogFailed(logger, provider, operation, (int)response.StatusCode, error.Kind);
            return error;
        }
    }

    internal static Error Failure(DnsProvider provider, HttpStatusCode status, DnsHostFault fault, TimeSpan? retryAfter)
    {
        string said = string.IsNullOrWhiteSpace(fault.Message) ? $"HTTP {(int)status}" : fault.Message.Trim().TrimEnd('.');
        if (fault.NotFound)
        {
            return Error.NotFound($"{provider.Name()}: {said}.");
        }

        if (fault.TokenRefused || status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return Error.Validation($"{provider.Name()} refused the credential ({said}). Check that it is current and may edit the domain's zone.", "dnsToken");
        }

        if (fault.Throttled || status == HttpStatusCode.TooManyRequests)
        {
            return Error.BackendUnavailable($"{provider.Name()} is limiting requests. Try again in a few minutes.", retryAfter is { } delta ? (int)Math.Ceiling(delta.TotalSeconds) : null);
        }

        if ((int)status >= 500)
        {
            return Error.BackendUnavailable($"{provider.Name()} failed ({said}). Try again shortly.");
        }

        return status == HttpStatusCode.NotFound
            ? Error.NotFound($"{provider.Name()}: {said}.")
            : Error.Validation($"{provider.Name()}: {said}.");
    }

    /// <summary>A body the host's reader cannot make sense of says nothing.</summary>
    private static DnsHostFault Read(Func<string, DnsHostFault> fault, string body)
    {
        try
        {
            return body.Length == 0 ? new DnsHostFault(null) : fault(body);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or System.Xml.XmlException or InvalidOperationException or FormatException)
        {
            return new DnsHostFault(null);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Provider} call {Operation} failed: HTTP {Status} → {Kind}")]
    private static partial void LogFailed(ILogger logger, DnsProvider provider, string operation, int status, ErrorKind kind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Provider} unreachable ({Operation}): {Reason}")]
    private static partial void LogUnreachable(ILogger logger, DnsProvider provider, string operation, string reason);
}

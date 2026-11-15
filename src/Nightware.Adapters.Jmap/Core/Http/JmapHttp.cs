using System.Text.Json;
using Nightware.Adapters.Jmap.Core.Errors;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;
using Polly;

namespace Nightware.Adapters.Jmap.Core.Http;

/// <summary>Named HTTP clients of the adapter.</summary>
internal static class JmapHttpClients
{
    /// <summary>Session fetch and API POSTs: short timeouts, retry for safe requests.</summary>
    public const string Api = "Nightware.Jmap";

    /// <summary>Uploads and downloads: long timeout, retry for downloads only.</summary>
    public const string Blob = "Nightware.Jmap.Blob";

    /// <summary>Unauthenticated health probe; no retries.</summary>
    public const string Health = "Nightware.Jmap.Health";
}

/// <summary>Transport-level helpers shared by the session provider, client and blob service.</summary>
internal static class JmapHttp
{
    /// <summary>Sends a request, turning transport failures into <see cref="ErrorKind.BackendUnavailable"/>. Caller cancellation propagates.</summary>
    public static async Task<Result<HttpResponseMessage>> SendAsync(HttpClient client, HttpRequestMessage request, HttpCompletionOption completion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        try
        {
            return await client.SendAsync(request, completion, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (ExecutionRejectedException ex)
        {
            return Error.BackendUnavailable($"The mail server did not answer in time ({ex.GetType().Name}).");
        }
        catch (OperationCanceledException)
        {
            return Error.BackendUnavailable("The mail server did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            return Error.BackendUnavailable($"The mail server is unreachable ({ex.HttpRequestError}).");
        }
    }

    /// <summary>Maps a failed response, reading a JMAP problem document when there is one.</summary>
    /// <param name="strategy">How the request's credential was obtained (a 401 means different things; see <see cref="JmapErrors.FromStatus"/>).</param>
    public static async Task<Error> ReadFailureAsync(HttpResponseMessage response, TimeProvider time, string context, CredentialStrategy strategy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(time);
        var retryAfter = JmapErrors.RetryAfterSeconds(response, time.GetUtcNow());
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null && mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                var problem = JsonSerializer.Deserialize(bytes, JmapJsonContext.Default.ProblemDetails);
                if (problem?.Type is not null)
                {
                    return JmapErrors.FromProblem(problem, response.StatusCode, retryAfter, strategy);
                }
            }
            catch (JsonException)
            {
                // Not a problem document; fall back to the status.
            }
        }

        return JmapErrors.FromStatus(response.StatusCode, retryAfter, context, strategy);
    }

    public static bool IsRedirect(HttpResponseMessage response) =>
        (int)response.StatusCode is 301 or 302 or 303 or 307 or 308 && response.Headers.Location is not null;
}

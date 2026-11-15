using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Nightware.Adapters.Jmap.Core.Auth;
using Nightware.Adapters.Jmap.Core.Errors;
using Nightware.Adapters.Jmap.Core.Http;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Core.Session;

/// <summary>Fetches and caches the JMAP Session per credential identity.</summary>
internal interface IJmapSessionProvider
{
    Task<Result<JmapSession>> GetAsync(BackendCredential credential, CancellationToken cancellationToken);

    /// <summary>Drops the cached session (after a 401, or when it is known to be stale).</summary>
    void Invalidate(BackendCredential credential);

    /// <summary>Drops the cached session when a response's <c>sessionState</c> differs from it (RFC 8620 §3.4).</summary>
    void ObserveSessionState(BackendCredential credential, JmapSession session, string? sessionState);
}

/// <inheritdoc />
internal sealed class JmapSessionProvider(
    IHttpClientFactory httpClientFactory,
    IMemoryCache cache,
    IOptions<JmapOptions> options,
    TimeProvider time) : IJmapSessionProvider
{
    private const int MaxRedirects = 3;

    public async Task<Result<JmapSession>> GetAsync(BackendCredential credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var key = CacheKey(credential);
        if (cache.TryGetValue(key, out JmapSession? cached) && cached is not null)
        {
            return cached;
        }

        var fetched = await FetchAsync(credential, cancellationToken).ConfigureAwait(false);
        if (fetched.IsSuccess)
        {
            cache.Set(key, fetched.Value, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = options.Value.SessionCacheDuration });
        }

        return fetched;
    }

    public void Invalidate(BackendCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        cache.Remove(CacheKey(credential));
    }

    public void ObserveSessionState(BackendCredential credential, JmapSession session, string? sessionState)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!string.IsNullOrEmpty(sessionState) && !string.Equals(sessionState, session.State, StringComparison.Ordinal))
        {
            Invalidate(credential);
        }
    }

    private string CacheKey(BackendCredential credential) =>
        "nightware:jmap:session:" + JmapAuthorization.IdentityKey(credential, options.Value.MasterUserSeparator);

    private async Task<Result<JmapSession>> FetchAsync(BackendCredential credential, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var sessionUrl = settings.SessionUrl ?? throw new InvalidOperationException("Jmap:SessionUrl is not configured.");
        var client = httpClientFactory.CreateClient(JmapHttpClients.Api);
        var url = sessionUrl;

        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url).WithCredential(credential);
            request.Headers.Accept.ParseAdd("application/json");
            var sent = await JmapHttp.SendAsync(client, request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
            if (!sent.IsSuccess)
            {
                return sent.Error;
            }

            using var response = sent.Value;
            if (JmapHttp.IsRedirect(response))
            {
                // Followed here, not by the handler, so the credential is re-applied — and only on the same origin.
                var next = new Uri(url, response.Headers.Location!);
                if (hop >= MaxRedirects || !SameOrigin(sessionUrl, next))
                {
                    return JmapErrors.Unexpected($"The JMAP session URL redirected to {next.GetLeftPart(UriPartial.Path)}, which is not followed.");
                }

                url = next;
                continue;
            }

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return await JmapHttp.ReadFailureAsync(response, time, "session", credential.Strategy, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                var document = JsonSerializer.Deserialize(bytes, JmapJsonContext.Default.JmapSessionDocument)
                    ?? throw new JsonException("Empty session document.");
                return JmapSession.Create(document, url, settings.UseSessionUrlOrigin, settings.MaxObjectsInGetFallback, settings.MaxCallsInRequestFallback);
            }
            catch (JsonException ex)
            {
                return Error.BackendUnavailable($"The JMAP session document is unreadable ({ex.Message}).");
            }
        }
    }

    private static bool SameOrigin(Uri a, Uri b) =>
        string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;
}

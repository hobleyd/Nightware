using Nightware.Application.Common.Identity;

namespace Nightware.Adapters.Jmap.Core.Auth;

/// <summary>
/// Per-request metadata carried on <see cref="HttpRequestMessage.Options"/>. The credential
/// travels with the request because <c>IHttpClientFactory</c> builds handlers in its own DI
/// scope, where the caller's scoped <see cref="IBackendCredentials"/> is not reachable.
/// </summary>
internal static class JmapRequestOptions
{
    /// <summary>The credential (from <see cref="IBackendCredentials"/>) to present.</summary>
    public static readonly HttpRequestOptionsKey<BackendCredential> Credential = new("Nightware.Jmap.Credential");

    /// <summary>Set to true for deliberately unauthenticated requests (health check).</summary>
    public static readonly HttpRequestOptionsKey<bool> Anonymous = new("Nightware.Jmap.Anonymous");

    /// <summary>True when the request has no side effects and may be retried.</summary>
    public static readonly HttpRequestOptionsKey<bool> Safe = new("Nightware.Jmap.Safe");

    public static HttpRequestMessage WithCredential(this HttpRequestMessage request, BackendCredential credential)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Options.Set(Credential, credential);
        return request;
    }

    public static HttpRequestMessage AsAnonymous(this HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Options.Set(Anonymous, true);
        return request;
    }

    public static HttpRequestMessage MarkSafe(this HttpRequestMessage request, bool safe = true)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Options.Set(Safe, safe);
        return request;
    }

    /// <summary>GET/HEAD are always safe; a POST only when the client marked it so.</summary>
    public static bool IsSafe(this HttpRequestMessage? request) =>
        request is not null
        && (request.Method == HttpMethod.Get
            || request.Method == HttpMethod.Head
            || (request.Options.TryGetValue(Safe, out var safe) && safe));
}

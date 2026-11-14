namespace Nightware.Adapters.Directory.Core;

/// <summary>Names and request options shared by the Keycloak HTTP clients.</summary>
internal static class KeycloakHttp
{
    /// <summary>Named client for the token endpoint and discovery.</summary>
    public const string TokenClientName = "Nightware.Keycloak.Token";

    /// <summary>
    /// Opt-in marker: the resilience pipeline retries a request only when it is a GET/HEAD or carries
    /// this option. Token exchange never sets it — a failing exchange is not replayed blindly.
    /// </summary>
    public static readonly HttpRequestOptionsKey<bool> RetrySafe = new("Nightware.Keycloak.RetrySafe");

    public static void MarkRetrySafe(this HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Options.Set(RetrySafe, true);
    }

    public static bool IsRetrySafe(this HttpRequestMessage? request) =>
        request is not null
        && (request.Method == HttpMethod.Get
            || request.Method == HttpMethod.Head
            || (request.Options.TryGetValue(RetrySafe, out bool safe) && safe));

    /// <summary>Seconds from a <c>Retry-After</c> header, if present.</summary>
    public static int? RetryAfterSeconds(HttpResponseMessage response, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (response.Headers.RetryAfter is not { } retryAfter)
        {
            return null;
        }

        TimeSpan? delay = retryAfter.Delta ?? (retryAfter.Date - timeProvider.GetUtcNow());
        return delay is { } d ? (int)Math.Max(0, Math.Ceiling(d.TotalSeconds)) : null;
    }
}

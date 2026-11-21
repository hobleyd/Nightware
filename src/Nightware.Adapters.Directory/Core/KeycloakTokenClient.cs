using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Polly;

using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Directory.Core;

/// <summary>
/// Token-endpoint client. Singleton: it owns the cached service-account token, and creates a pooled
/// <see cref="HttpClient"/> per call from <see cref="IHttpClientFactory"/>.
/// </summary>
/// <remarks>
/// Token exchange follows Keycloak 26 "standard token exchange" (V2, supported since 26.2):
/// https://www.keycloak.org/securing-apps/token-exchange — "Request and response parameters".
/// The requester must be a confidential client with "Standard token exchange" enabled
/// (<c>standard.token.exchange.enabled</c>), the subject token's <c>aud</c> must contain the requester,
/// <c>subject_token_type</c> must be <c>…:access_token</c> (the only supported type), and
/// <c>audience</c> can only narrow the audiences already available to the requester (it never adds
/// one — here via the <c>stalwart</c>/<c>nextcloud</c> audience mappers on <c>nightware-api</c>). The
/// issued token's lifetime is capped by the subject token's <c>exp</c>. The client authenticates with
/// <c>client_secret_basic</c> (RFC 6749 §2.3.1).
/// </remarks>
internal sealed partial class KeycloakTokenClient(
    IHttpClientFactory httpClientFactory,
    IOptions<DirectoryOptions> options,
    TimeProvider timeProvider,
    ILogger<KeycloakTokenClient> logger) : IKeycloakTokenClient, IDisposable
{
    internal const string TokenExchangeGrant = "urn:ietf:params:oauth:grant-type:token-exchange";
    internal const string AccessTokenType = "urn:ietf:params:oauth:token-type:access_token";
    internal const string RefreshTokenType = "urn:ietf:params:oauth:token-type:refresh_token";

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private CachedToken? _serviceAccountToken;

    private static KeyValuePair<string, string>[] ClientCredentialsForm(string scope) =>
        string.IsNullOrWhiteSpace(scope)
            ? [new("grant_type", "client_credentials")]
            : [new("grant_type", "client_credentials"), new("scope", scope)];

    public async Task<Result<AccessToken>> GetServiceAccountTokenAsync(CancellationToken cancellationToken)
    {
        if (TryGetFresh() is { } fresh)
        {
            return fresh;
        }

        await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller may have refreshed while we waited (single-flight).
            if (TryGetFresh() is { } refreshed)
            {
                return refreshed;
            }

            Result<AccessToken> result = await RequestAsync(
                    ClientCredentialsForm(options.Value.ServiceAccountScope),
                    retrySafe: true,
                    static (status, _) => KeycloakTokenErrorClassifier.ClassifyClientCredentials(status),
                    "client_credentials",
                    cancellationToken)
                .ConfigureAwait(false);

            if (result.IsSuccess)
            {
                Volatile.Write(ref _serviceAccountToken, new CachedToken(result.Value, RefreshAfter(result.Value)));
                LogServiceAccountTokenIssued(logger, result.Value.ExpiresAt);
            }

            return result;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public void InvalidateServiceAccountToken(AccessToken rejected)
    {
        ArgumentNullException.ThrowIfNull(rejected);
        CachedToken? current = Volatile.Read(ref _serviceAccountToken);
        if (current is not null && ReferenceEquals(current.Token, rejected))
        {
            Interlocked.CompareExchange(ref _serviceAccountToken, null, current);
        }
    }

    public Task<Result<AccessToken>> ExchangeAsync(string subjectToken, string audience, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(audience);

        return RequestAsync(
            [
                new("grant_type", TokenExchangeGrant),
                new("subject_token", subjectToken),
                new("subject_token_type", AccessTokenType),
                new("requested_token_type", AccessTokenType),
                new("audience", audience),
            ],
            retrySafe: false,
            KeycloakTokenErrorClassifier.ClassifyExchange,
            $"token-exchange(audience={audience})",
            cancellationToken);
    }

    public async Task<Result<TokenPair>> ExchangeForRefreshTokenAsync(string subjectToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectToken);
        var response = await RequestCoreAsync(
            [
                new("grant_type", TokenExchangeGrant),
                new("subject_token", subjectToken),
                new("subject_token_type", AccessTokenType),
                new("requested_token_type", RefreshTokenType),
            ],
            retrySafe: false,
            KeycloakTokenErrorClassifier.ClassifyExchange,
            "token-exchange(refresh_token)",
            cancellationToken).ConfigureAwait(false);
        return Pair(response, "token-exchange(refresh_token)");
    }

    public async Task<Result<TokenPair>> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        var response = await RequestCoreAsync(
            [new("grant_type", "refresh_token"), new("refresh_token", refreshToken)],
            retrySafe: false,
            KeycloakTokenErrorClassifier.ClassifyExchange,
            "refresh",
            cancellationToken).ConfigureAwait(false);
        return Pair(response, "refresh");
    }

    private Result<TokenPair> Pair(Result<(AccessToken Access, string? Refresh)> response, string operation)
    {
        if (!response.IsSuccess)
        {
            return response.Error;
        }

        if (response.Value.Refresh is not { Length: > 0 } refresh)
        {
            LogMalformedResponse(logger, operation);
            return Error.BackendUnavailable("The identity provider issued no refresh token; is refresh-token exchange enabled for the client?");
        }

        return new TokenPair(response.Value.Access, refresh);
    }

    public void Dispose() => _refreshGate.Dispose();

    private AccessToken? TryGetFresh()
    {
        CachedToken? cached = Volatile.Read(ref _serviceAccountToken);
        return cached is not null && timeProvider.GetUtcNow() < cached.RefreshAfter ? cached.Token : null;
    }

    private DateTimeOffset RefreshAfter(AccessToken token)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        TimeSpan lifetime = token.ExpiresAt - now;
        TimeSpan skew = options.Value.ServiceAccountTokenRefreshSkew;

        // A lifetime shorter than the skew would never be "fresh"; use half of it instead.
        return lifetime > skew * 2 ? token.ExpiresAt - skew : now + (lifetime / 2);
    }

    private async Task<Result<AccessToken>> RequestAsync(
        IEnumerable<KeyValuePair<string, string>> form,
        bool retrySafe,
        Func<System.Net.HttpStatusCode, OAuthErrorResponse?, KeycloakTokenFailure> classify,
        string operation,
        CancellationToken cancellationToken)
    {
        var response = await RequestCoreAsync(form, retrySafe, classify, operation, cancellationToken).ConfigureAwait(false);
        return response.IsSuccess ? response.Value.Access : response.Error;
    }

    private async Task<Result<(AccessToken Access, string? Refresh)>> RequestCoreAsync(
        IEnumerable<KeyValuePair<string, string>> form,
        bool retrySafe,
        Func<System.Net.HttpStatusCode, OAuthErrorResponse?, KeycloakTokenFailure> classify,
        string operation,
        CancellationToken cancellationToken)
    {
        DirectoryOptions settings = options.Value;
        using HttpRequestMessage request = new(HttpMethod.Post, settings.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Authorization = ClientSecretBasic(settings.ClientId, settings.ClientSecret);
        if (retrySafe)
        {
            request.MarkRetrySafe();
        }

        HttpClient http = httpClientFactory.CreateClient(KeycloakHttp.TokenClientName);
        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return await ReadTokenAsync(response, operation, cancellationToken).ConfigureAwait(false);
            }

            OAuthErrorResponse? error = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
            KeycloakTokenFailure failure = classify(response.StatusCode, error);
            int? retryAfter = KeycloakHttp.RetryAfterSeconds(response, timeProvider);
            return ToError(failure, operation, (int)response.StatusCode, error, retryAfter);
        }
        catch (HttpRequestException ex)
        {
            LogUnreachable(logger, operation, ex.Message);
            return Error.BackendUnavailable("The identity provider is unreachable.");
        }
        catch (Exception ex) when (ex is TimeoutException or ExecutionRejectedException)
        {
            LogUnreachable(logger, operation, "timed out");
            return Error.BackendUnavailable("The identity provider timed out.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogUnreachable(logger, operation, "timed out");
            return Error.BackendUnavailable("The identity provider timed out.");
        }
    }

    private async Task<Result<(AccessToken Access, string? Refresh)>> ReadTokenAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        TokenEndpointResponse? body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<TokenEndpointResponse>(cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            body = null;
        }

        if (body is not { AccessToken.Length: > 0, ExpiresIn: > 0 }
            || !string.Equals(body.TokenType, "Bearer", StringComparison.OrdinalIgnoreCase))
        {
            LogMalformedResponse(logger, operation);
            return Error.BackendUnavailable("The identity provider returned an unusable token response.");
        }

        return (new AccessToken(body.AccessToken, timeProvider.GetUtcNow().AddSeconds(body.ExpiresIn.Value)), body.RefreshToken);
    }

    private static async Task<OAuthErrorResponse?> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<OAuthErrorResponse>(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private Error ToError(KeycloakTokenFailure failure, string operation, int status, OAuthErrorResponse? error, int? retryAfter)
    {
        // error / error_description are Keycloak's own fixed strings (they echo the audience at most),
        // never the subject token, so they are safe to log.
        string code = error?.Error ?? "(none)";
        string description = error?.ErrorDescription ?? "(none)";
        switch (failure)
        {
            case KeycloakTokenFailure.SubjectRejected:
                LogSubjectRejected(logger, operation, status, code, description);
                return Error.BackendAuthFailed("The access token was rejected by the identity provider; sign in again.");
            case KeycloakTokenFailure.Misconfigured:
                LogMisconfigured(logger, operation, status, code, description);
                return Error.BackendUnavailable("The service is misconfigured: the identity provider rejected its request.");
            default:
                LogUnavailable(logger, operation, status, code);
                return Error.BackendUnavailable("The identity provider is unavailable.", retryAfter);
        }
    }

    private static AuthenticationHeaderValue ClientSecretBasic(string clientId, string clientSecret)
    {
        // RFC 6749 §2.3.1: id and secret are form-urlencoded before being joined and base64-encoded.
        string pair = $"{Uri.EscapeDataString(clientId)}:{Uri.EscapeDataString(clientSecret)}";
        return new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(pair)));
    }

    private sealed record CachedToken(AccessToken Token, DateTimeOffset RefreshAfter);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Keycloak service-account token issued, expires {ExpiresAt:O}")]
    private static partial void LogServiceAccountTokenIssued(ILogger logger, DateTimeOffset expiresAt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Keycloak {Operation} rejected the subject token: HTTP {Status} {Error} ({Description})")]
    private static partial void LogSubjectRejected(ILogger logger, string operation, int status, string error, string description);

    [LoggerMessage(Level = LogLevel.Error, Message = "Keycloak {Operation} rejected the façade's own request — check Directory/BackendCredentials configuration and the realm: HTTP {Status} {Error} ({Description})")]
    private static partial void LogMisconfigured(ILogger logger, string operation, int status, string error, string description);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Keycloak {Operation} failed: HTTP {Status} {Error}")]
    private static partial void LogUnavailable(ILogger logger, string operation, int status, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Keycloak {Operation} unreachable: {Reason}")]
    private static partial void LogUnreachable(ILogger logger, string operation, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Keycloak {Operation} returned a malformed token response")]
    private static partial void LogMalformedResponse(ILogger logger, string operation);
}

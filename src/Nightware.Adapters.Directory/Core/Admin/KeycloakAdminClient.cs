using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Polly;

using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Directory.Core.Admin;

/// <summary>
/// Keycloak admin REST client (https://www.keycloak.org/docs-api/latest/rest-api/index.html, "Users").
/// A typed <see cref="HttpClient"/>; the service-account token comes from the singleton
/// <see cref="IKeycloakTokenClient"/>. A 401 drops the cached token and retries once with a fresh
/// one (covers revocation and Keycloak restarts).
/// </summary>
internal sealed partial class KeycloakAdminClient(
    HttpClient http,
    IKeycloakTokenClient tokens,
    IOptions<DirectoryOptions> options,
    TimeProvider timeProvider,
    ILogger<KeycloakAdminClient> logger) : IKeycloakAdminClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<Result<KeycloakUser>> GetUserAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, UserUrl(id)),
            ReadJsonAsync<KeycloakUser>,
            cancellationToken);
    }

    public Task<Result<IReadOnlyList<KeycloakUser>>> FindUsersAsync(KeycloakUserQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, Url("users" + query.ToQueryString(includePaging: true))),
            async (response, ct) => (await ReadJsonAsync<List<KeycloakUser>>(response, ct).ConfigureAwait(false))
                .Map(static IReadOnlyList<KeycloakUser> (users) => users),
            cancellationToken);
    }

    public Task<Result<int>> CountUsersAsync(KeycloakUserQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return SendAsync(
            () => new HttpRequestMessage(HttpMethod.Get, Url("users/count" + query.ToQueryString(includePaging: false))),
            ReadJsonAsync<int>,
            cancellationToken);
    }

    public async Task<Result<KeycloakUser>> FindUserByEmailAsync(string email, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        Result<IReadOnlyList<KeycloakUser>> found = await FindUsersAsync(KeycloakUserQuery.ByEmail(email), cancellationToken).ConfigureAwait(false);
        return found.Bind(users => Single(users, u => string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase)));
    }

    public async Task<Result<KeycloakUser>> FindUserByUsernameAsync(string username, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        Result<IReadOnlyList<KeycloakUser>> found = await FindUsersAsync(KeycloakUserQuery.ByUsername(username), cancellationToken).ConfigureAwait(false);
        return found.Bind(users => Single(users, u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase)));
    }

    public async Task<Result<IReadOnlyDictionary<string, IReadOnlyList<string>>>> GetUserAttributesAsync(string id, CancellationToken cancellationToken)
    {
        Result<KeycloakUser> user = await GetUserAsync(id, cancellationToken).ConfigureAwait(false);
        return user.Map(static u => u.Attributes ?? new Dictionary<string, IReadOnlyList<string>>());
    }

    public async Task<Result<KeycloakUser>> UpdateUserAttributesAsync(
        string id,
        IReadOnlyDictionary<string, IReadOnlyList<string>?> changes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(changes);

        Result<JsonObject> current = await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Get, UserUrl(id)),
                ReadJsonAsync<JsonObject>,
                cancellationToken)
            .ConfigureAwait(false);
        if (!current.IsSuccess)
        {
            return current.Error;
        }

        JsonObject representation = current.Value;
        JsonObject attributes = representation["attributes"] as JsonObject ?? [];
        foreach ((string name, IReadOnlyList<string>? values) in changes)
        {
            if (values is null)
            {
                attributes.Remove(name);
            }
            else
            {
                attributes[name] = new JsonArray([.. values.Select(static v => JsonValue.Create(v))]);
            }
        }

        representation["attributes"] = attributes;
        string body = representation.ToJsonString();

        Result<bool> written = await SendAsync(
                () => new HttpRequestMessage(HttpMethod.Put, UserUrl(id))
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
                },
                static (_, _) => Task.FromResult(Result<bool>.Success(true)),
                cancellationToken)
            .ConfigureAwait(false);
        if (!written.IsSuccess)
        {
            return written.Error;
        }

        LogAttributesUpdated(logger, id, changes.Count);
        return representation.Deserialize<KeycloakUser>(Json)!;
    }

    private Uri Url(string relative) => new(options.Value.AdminRealmUrl, relative);

    private Uri UserUrl(string id) => Url("users/" + Uri.EscapeDataString(id));

    private static Result<KeycloakUser> Single(IReadOnlyList<KeycloakUser> users, Func<KeycloakUser, bool> matches)
    {
        // Keycloak's exact match is case-insensitive for email/username; re-check so a lookup can never
        // return a different person, and refuse to guess when several match.
        KeycloakUser[] exact = [.. users.Where(matches)];
        return exact.Length == 1 ? exact[0] : Error.NotFound("The user was not found in the directory.");
    }

    private async Task<Result<T>> SendAsync<T>(
        Func<HttpRequestMessage> buildRequest,
        Func<HttpResponseMessage, CancellationToken, Task<Result<T>>> onSuccess,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            Result<AccessToken> token = await tokens.GetServiceAccountTokenAsync(cancellationToken).ConfigureAwait(false);
            if (!token.IsSuccess)
            {
                return token.Error;
            }

            using HttpRequestMessage request = buildRequest();
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value.Value);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                LogUnreachable(logger, request.Method.Method, ex.Message);
                return Error.BackendUnavailable("The directory is unreachable.");
            }
            catch (Exception ex) when (ex is TimeoutException or ExecutionRejectedException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                LogUnreachable(logger, request.Method.Method, "timed out");
                return Error.BackendUnavailable("The directory timed out.");
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    LogTokenRejected(logger);
                    tokens.InvalidateServiceAccountToken(token.Value);
                    continue;
                }

                if (response.IsSuccessStatusCode)
                {
                    return await onSuccess(response, cancellationToken).ConfigureAwait(false);
                }

                string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                Error error = KeycloakAdminErrorMapper.Map(response.StatusCode, body, KeycloakHttp.RetryAfterSeconds(response, timeProvider));
                LogFailed(logger, request.Method.Method, request.RequestUri?.AbsolutePath ?? string.Empty, (int)response.StatusCode, error.Kind);
                return error;
            }
        }
    }

    private async Task<Result<T>> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            T? value = await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken).ConfigureAwait(false);
            if (value is not null)
            {
                return value;
            }
        }
        catch (JsonException)
        {
            // fall through
        }

        LogMalformed(logger, typeof(T).Name);
        return Error.BackendUnavailable("The directory returned a malformed response.");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Keycloak admin API rejected the service-account token; refreshing it and retrying once")]
    private static partial void LogTokenRejected(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Keycloak admin {Method} {Path} failed: HTTP {Status} → {Kind}")]
    private static partial void LogFailed(ILogger logger, string method, string path, int status, ErrorKind kind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Keycloak admin {Method} unreachable: {Reason}")]
    private static partial void LogUnreachable(ILogger logger, string method, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Keycloak admin API returned a malformed {Type}")]
    private static partial void LogMalformed(ILogger logger, string type);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Updated {Count} attribute(s) of Keycloak user {UserId}")]
    private static partial void LogAttributesUpdated(ILogger logger, string userId, int count);
}

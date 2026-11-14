using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Directory.Core.Admin;

/// <summary>Maps a non-2xx admin REST response to a domain <see cref="Error"/>.</summary>
internal static class KeycloakAdminErrorMapper
{
    public static Error Map(HttpStatusCode status, string? body, int? retryAfterSeconds)
    {
        string detail = ReadMessage(body);
        return status switch
        {
            HttpStatusCode.BadRequest => Error.Validation(Describe("The directory rejected the request", detail)),
            HttpStatusCode.Unauthorized => Error.BackendUnavailable("The directory rejected the service's credentials."),
            HttpStatusCode.Forbidden => Error.Forbidden("The directory service account is not permitted to do this."),
            HttpStatusCode.NotFound => Error.NotFound("The user was not found in the directory."),
            HttpStatusCode.Conflict => Error.Conflict(Describe("The directory reported a conflict", detail)),
            HttpStatusCode.TooManyRequests => new Error(ErrorKind.Throttled, "activityLimitReached", "The directory is throttling requests.")
            {
                RetryAfterSeconds = retryAfterSeconds,
            },
            _ when (int)status >= 500 => Error.BackendUnavailable("The directory is unavailable.", retryAfterSeconds),
            _ => new Error(ErrorKind.Unknown, "generalException", $"The directory returned HTTP {(int)status}."),
        };
    }

    private static string Describe(string prefix, string detail) => detail.Length == 0 ? prefix + "." : $"{prefix}: {detail}";

    /// <summary>Keycloak error bodies are <c>{"errorMessage": …}</c> or <c>{"error": …, "error_description": …}</c>.</summary>
    private static string ReadMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        try
        {
            AdminErrorBody? parsed = JsonSerializer.Deserialize<AdminErrorBody>(body);
            return parsed?.ErrorMessage ?? parsed?.ErrorDescription ?? parsed?.Error ?? string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private sealed record AdminErrorBody(
        [property: JsonPropertyName("errorMessage")] string? ErrorMessage,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("error_description")] string? ErrorDescription);
}

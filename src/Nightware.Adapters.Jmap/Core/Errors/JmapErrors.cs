using System.Net;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Core.Errors;

/// <summary>
/// The single place JMAP failures become Domain <see cref="Error"/>s: HTTP status,
/// request-level problem details, method-level <c>error</c> responses and per-object
/// <see cref="SetError"/>s. See the table in the adapter README.
/// </summary>
internal static class JmapErrors
{
    public const string ProblemPrefix = "urn:ietf:params:jmap:error:";

    public static Error Throttled(string message, int? retryAfterSeconds) =>
        new(ErrorKind.Throttled, "activityLimitReached", message) { RetryAfterSeconds = retryAfterSeconds };

    public static Error PayloadTooLarge(string message, string? target = null) =>
        new(ErrorKind.PayloadTooLarge, "requestEntityTooLarge", message, target);

    public static Error PreconditionFailed(string message) =>
        new(ErrorKind.PreconditionFailed, "preconditionFailed", message);

    public static Error Unexpected(string message) =>
        new(ErrorKind.Unknown, "generalException", message);

    /// <summary>Maps a non-success HTTP response (any endpoint) when no JMAP problem document refines it.</summary>
    public static Error FromStatus(HttpStatusCode status, int? retryAfterSeconds, string context)
    {
        var code = (int)status;
        return status switch
        {
            HttpStatusCode.Unauthorized => Error.BackendAuthFailed($"The mail server rejected the credentials ({context})."),
            HttpStatusCode.Forbidden => Error.Forbidden($"The mail server denied access ({context})."),
            HttpStatusCode.NotFound => Error.NotFound($"Not found on the mail server ({context})."),
            HttpStatusCode.RequestEntityTooLarge => PayloadTooLarge($"The mail server rejected the request size ({context})."),
            HttpStatusCode.TooManyRequests => Throttled($"The mail server is throttling requests ({context}).", retryAfterSeconds),
            _ when code >= 500 => Error.BackendUnavailable($"The mail server failed with HTTP {code} ({context}).", retryAfterSeconds),
            _ => Unexpected($"The mail server answered HTTP {code} ({context})."),
        };
    }

    /// <summary>Maps a request-level error (RFC 8620 §3.6.1).</summary>
    public static Error FromProblem(ProblemDetails problem, HttpStatusCode status, int? retryAfterSeconds)
    {
        ArgumentNullException.ThrowIfNull(problem);
        var type = problem.Type ?? string.Empty;
        var detail = problem.Detail ?? problem.Title ?? type;
        if (!type.StartsWith(ProblemPrefix, StringComparison.Ordinal))
        {
            return FromStatus(status, retryAfterSeconds, detail);
        }

        return type[ProblemPrefix.Length..] switch
        {
            "unknownCapability" => Error.Unsupported($"The mail server lacks a required capability: {detail}"),
            "limit" => FromLimit(problem.Limit, detail, retryAfterSeconds),
            "notJSON" or "notRequest" => Unexpected($"The mail server rejected the request as malformed ({type}): {detail}"),
            _ => FromStatus(status, retryAfterSeconds, detail),
        };
    }

    /// <summary>Maps a method-level <c>error</c> response (RFC 8620 §3.6.2, RFC 8621).</summary>
    public static Error FromMethodError(MethodError error, string method)
    {
        ArgumentNullException.ThrowIfNull(error);
        var message = $"{method}: {error.Type}{(string.IsNullOrEmpty(error.Description) ? string.Empty : $" — {error.Description}")}";
        return error.Type switch
        {
            "cannotCalculateChanges" or "tooManyChanges" => Error.SyncStateGone(message),
            "accountNotFound" or "fromAccountNotFound" => Error.NotFound(message),
            "forbidden" or "accountReadOnly" => Error.Forbidden(message),
            "accountNotSupportedByMethod" or "fromAccountNotSupportedByMethod" or "unknownMethod"
                or "unsupportedFilter" or "unsupportedSort" => Error.Unsupported(message),
            "invalidArguments" or "anchorNotFound" => Error.Validation(message),
            "requestTooLarge" => PayloadTooLarge(message),
            "stateMismatch" => PreconditionFailed(message),
            "rateLimit" or "overRateLimit" => Throttled(message, null),
            "serverUnavailable" => Error.BackendUnavailable(message),
            // serverFail, serverPartialFail, invalidResultReference (our bug) and anything new.
            _ => Unexpected(message),
        };
    }

    /// <summary>Maps one entry of <c>notCreated</c>/<c>notUpdated</c>/<c>notDestroyed</c> (RFC 8620 §5.3, RFC 8621).</summary>
    public static Error FromSetError(SetError error, string objectId)
    {
        ArgumentNullException.ThrowIfNull(error);
        var message = $"{error.Type} ({objectId}){(string.IsNullOrEmpty(error.Description) ? string.Empty : $": {error.Description}")}";
        return error.Type switch
        {
            "notFound" or "blobNotFound" => Error.NotFound(message, objectId),
            "forbidden" or "forbiddenFrom" or "forbiddenMailFrom" or "forbiddenToSend" => Error.Forbidden(message),
            "overQuota" => new Error(ErrorKind.PayloadTooLarge, "ErrorQuotaExceeded", message, objectId),
            "tooLarge" => PayloadTooLarge(message, objectId),
            "rateLimit" => Throttled(message, null),
            "invalidProperties" => Error.Validation(message, error.Properties is { Count: > 0 } props ? string.Join(',', props) : objectId),
            "invalidPatch" or "singleton" or "tooManyKeywords" or "tooManyMailboxes" or "invalidEmail"
                or "tooManyRecipients" or "noRecipients" or "invalidRecipients" => Error.Validation(message, objectId),
            "willDestroy" or "mailboxHasChild" or "mailboxHasEmail" or "alreadyExists" or "cannotUnsend" => Error.Conflict(message, objectId),
            "stateMismatch" => PreconditionFailed(message),
            _ => Unexpected(message),
        };
    }

    /// <summary>
    /// Seconds from a <c>Retry-After</c> header (delta or HTTP date), rounded up, or null.
    /// </summary>
    public static int? RetryAfterSeconds(HttpResponseMessage response, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(response);
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return (int)Math.Ceiling(Math.Max(0, delta.TotalSeconds));
        }

        if (retryAfter?.Date is { } date)
        {
            return (int)Math.Ceiling(Math.Max(0, (date - now).TotalSeconds));
        }

        return null;
    }

    private static Error FromLimit(string? limit, string detail, int? retryAfterSeconds) => limit switch
    {
        "maxConcurrentRequests" or "maxConcurrentUpload" => Throttled($"The mail server's {limit} limit was reached.", retryAfterSeconds),
        "maxSizeRequest" or "maxSizeUpload" => PayloadTooLarge($"The request exceeds the mail server's {limit} limit.", limit),
        _ => Unexpected($"The request exceeds the mail server's {limit ?? "unknown"} limit: {detail}"),
    };
}

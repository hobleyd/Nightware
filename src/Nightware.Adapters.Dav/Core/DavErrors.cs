using System.Net;
using System.Xml.Linq;
using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Core;

/// <summary>What a request meant, so status codes that depend on intent map correctly.</summary>
public enum DavRequestIntent
{
    Default,
    /// <summary>A PUT with <c>If-None-Match: *</c>: a 412 means "already exists" (Conflict).</summary>
    CreateOnly,
    /// <summary>A write with <c>If-Match</c>: a 412 means "stale ETag" (PreconditionFailed).</summary>
    Conditional,
}

/// <summary>
/// Maps Nextcloud/Sabre failures onto <see cref="Error"/>. The error body is read first:
/// Sabre names preconditions in a <c>DAV:error</c> element and those take priority.
/// </summary>
public static class DavErrors
{
    public const string PreconditionFailedCode = "preconditionFailed";
    public const string PayloadTooLargeCode = "payloadTooLarge";
    public const string ThrottledCode = "activityLimitReached";
    public const string ItemExistsCode = "nameAlreadyExists";
    public const string UidConflictCode = "uidConflict";
    public const string BackendErrorCode = "generalException";

    public static Error PreconditionFailed(string message) =>
        new(ErrorKind.PreconditionFailed, PreconditionFailedCode, message);

    public static Error PayloadTooLarge(string message) =>
        new(ErrorKind.PayloadTooLarge, PayloadTooLargeCode, message);

    public static Error Throttled(string message, int? retryAfterSeconds) =>
        new(ErrorKind.Throttled, ThrottledCode, message) { RetryAfterSeconds = retryAfterSeconds };

    public static Error Unavailable(string message, int? retryAfterSeconds = null) =>
        Error.BackendUnavailable(message, retryAfterSeconds);

    /// <summary>
    /// A 401 from Nextcloud (ADR 0001 §42). Only a passed-through token is the caller's own, so only then does
    /// the client have to sign in again (<see cref="ErrorKind.BackendAuthFailed"/>, 401). A credential the
    /// façade obtained (token exchange) was minted from a subject token Keycloak had just accepted: its rejection
    /// means the façade is misconfigured (audience mapper, client), a 503 rather than a re-auth banner.
    /// </summary>
    public static Error Unauthorized(CredentialStrategy credential) => credential == CredentialStrategy.PassThrough
        ? Error.BackendAuthFailed("The groupware server rejected the credentials.")
        : Unavailable("The groupware server rejected the credential the façade obtained for it; check the token-exchange configuration (audience 'nextcloud').");

    /// <summary>Maps a non-success response. <paramref name="body"/> is the (possibly empty) response text.</summary>
    /// <param name="credential">How the request's credential was obtained; decides what a 401 means (<see cref="Unauthorized"/>).</param>
    public static Error Map(HttpStatusCode status, string? body, int? retryAfterSeconds, DavRequestIntent intent = DavRequestIntent.Default, CredentialStrategy credential = CredentialStrategy.PassThrough)
    {
        var conditions = DavXml.ParseErrorConditions(body);
        if (conditions.Contains(DavNames.ValidSyncToken))
        {
            return Error.SyncStateGone("The sync token is no longer valid; resynchronise.");
        }

        if (conditions.Contains(DavNames.NoUidConflict) || conditions.Contains(DavNames.CardDavNoUidConflict))
        {
            return new Error(ErrorKind.Conflict, UidConflictCode, "An item with the same UID already exists.");
        }

        if (conditions.Contains(DavNames.MaxResourceSize) || conditions.Contains(DavNames.CardDavMaxResourceSize))
        {
            return PayloadTooLarge("The item exceeds the server's maximum size.");
        }

        var code = (int)status;
        return code switch
        {
            400 or 415 or 422 => Error.Validation($"The groupware server rejected the request ({code})."),
            401 => Unauthorized(credential),
            403 => Error.Forbidden("Access to the groupware resource is denied."),
            404 or 410 => Error.NotFound("The specified object was not found in the store."),
            405 => Error.Conflict("The resource already exists or does not allow this operation."),
            409 or 423 => Error.Conflict($"The groupware server reported a conflict ({code})."),
            412 when intent == DavRequestIntent.CreateOnly => new Error(ErrorKind.Conflict, ItemExistsCode, "An item already exists at this location."),
            412 => PreconditionFailed("The item was changed by someone else (ETag mismatch)."),
            413 or 507 => PayloadTooLarge("The item exceeds the server's size limit or quota."),
            429 => Throttled("The groupware server is throttling requests.", retryAfterSeconds),
            503 => Unavailable("The groupware server is unavailable.", retryAfterSeconds),
            >= 500 => Unavailable($"The groupware server failed ({code})."),
            _ => new Error(ErrorKind.Unknown, BackendErrorCode, $"Unexpected groupware response ({code})."),
        };
    }

    /// <summary>Seconds from a <c>Retry-After</c> header (delta-seconds or HTTP-date).</summary>
    public static int? RetryAfterSeconds(HttpResponseMessage response, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(timeProvider);
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return (int)Math.Max(0, Math.Ceiling(delta.TotalSeconds));
        }

        if (retryAfter?.Date is { } date)
        {
            return (int)Math.Max(0, Math.Ceiling((date - timeProvider.GetUtcNow()).TotalSeconds));
        }

        return null;
    }

    internal static bool Contains(this IReadOnlyList<XName> names, XName name) => names.Any(n => n == name);
}

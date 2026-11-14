namespace Nightware.Domain.Kernel;

/// <summary>
/// Provider-neutral failure categories. The Api maps each to an HTTP status and
/// Graph <c>error.code</c> in exactly one place (<c>GraphErrorMapper</c>).
/// </summary>
public enum ErrorKind
{
    Unknown = 0,
    NotFound,
    Conflict,
    Validation,
    Unsupported,
    Unauthorized,
    Forbidden,
    /// <summary>A delta/sync token can no longer be resolved; the client must resync (HTTP 410).</summary>
    SyncStateGone,
    PayloadTooLarge,
    Throttled,
    BackendUnavailable,
    /// <summary>A backend rejected our credentials; the client must re-authenticate (HTTP 401).</summary>
    BackendAuthFailed,
    PreconditionFailed,
}

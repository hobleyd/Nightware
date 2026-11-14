namespace Nightware.Domain.Kernel;

/// <summary>A failure carried by <see cref="Result{T}"/>.</summary>
/// <param name="Kind">Category that decides the HTTP status.</param>
/// <param name="Code">Stable machine-readable code, emitted as Graph <c>error.code</c> unless the mapper overrides it.</param>
/// <param name="Message">Human-readable detail. Never include secrets or message content.</param>
/// <param name="Target">The offending parameter or property, when there is one.</param>
public sealed record Error(ErrorKind Kind, string Code, string Message, string? Target = null)
{
    /// <summary>Seconds the caller should wait before retrying (Throttled / BackendUnavailable).</summary>
    public int? RetryAfterSeconds { get; init; }

    public static Error NotFound(string message, string? target = null) =>
        new(ErrorKind.NotFound, "itemNotFound", message, target);

    public static Error Validation(string message, string? target = null) =>
        new(ErrorKind.Validation, "invalidRequest", message, target);

    public static Error Unsupported(string message, string? target = null) =>
        new(ErrorKind.Unsupported, "notImplemented", message, target);

    public static Error Conflict(string message, string? target = null) =>
        new(ErrorKind.Conflict, "conflict", message, target);

    public static Error Forbidden(string message) =>
        new(ErrorKind.Forbidden, "accessDenied", message);

    public static Error SyncStateGone(string message) =>
        new(ErrorKind.SyncStateGone, "syncStateNotFound", message);

    public static Error BackendUnavailable(string message, int? retryAfterSeconds = null) =>
        new(ErrorKind.BackendUnavailable, "serviceNotAvailable", message) { RetryAfterSeconds = retryAfterSeconds };

    public static Error BackendAuthFailed(string message) =>
        new(ErrorKind.BackendAuthFailed, "InvalidAuthenticationToken", message);
}

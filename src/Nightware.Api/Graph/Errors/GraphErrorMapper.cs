using Microsoft.AspNetCore.Http;

using Nightware.Domain.Kernel;

namespace Nightware.Api.Graph.Errors;

/// <summary>The HTTP status, Graph <c>error.code</c> and optional <c>Retry-After</c> for one <see cref="Error"/>.</summary>
public sealed record GraphErrorDescriptor(int StatusCode, string Code, string Message, int? RetryAfterSeconds);

/// <summary>
/// The one place <see cref="ErrorKind"/> becomes an HTTP status and a Graph
/// <c>error.code</c> (architecture.md, "Results and errors").
/// </summary>
/// <remarks>
/// <para>The status is decided by the kind alone. The code is the kind's
/// canonical Graph code unless the error carries a more specific one: an
/// adapter that says <c>ErrorFolderExists</c> instead of the generic
/// <c>conflict</c> keeps it. Kinds whose meaning the client keys on
/// (auth, sync state, throttling, availability, unknown) always use the
/// canonical code so a backend can never make them look like something else.</para>
/// <para>NightMail inspects only the status and <c>error.message</c>; codes
/// matter for fidelity with the golden fixtures.</para>
/// </remarks>
public static class GraphErrorMapper
{
    /// <summary>Retry-After used for 429/503 when the error does not say.</summary>
    public const int DefaultRetryAfterSeconds = 5;

    private sealed record KindMapping(int Status, string CanonicalCode, string? GenericCode, bool CodeIsFixed, bool HasRetryAfter = false);

    private static readonly Dictionary<ErrorKind, KindMapping> Mappings = new()
    {
        [ErrorKind.Unknown] = new(StatusCodes.Status500InternalServerError, "generalException", null, CodeIsFixed: true),
        [ErrorKind.NotFound] = new(StatusCodes.Status404NotFound, "ErrorItemNotFound", "itemNotFound", CodeIsFixed: false),
        [ErrorKind.Conflict] = new(StatusCodes.Status409Conflict, "conflict", "conflict", CodeIsFixed: false),
        [ErrorKind.Validation] = new(StatusCodes.Status400BadRequest, "invalidRequest", "invalidRequest", CodeIsFixed: false),
        [ErrorKind.Unsupported] = new(StatusCodes.Status501NotImplemented, "notImplemented", "notImplemented", CodeIsFixed: false),
        [ErrorKind.Unauthorized] = new(StatusCodes.Status401Unauthorized, "InvalidAuthenticationToken", null, CodeIsFixed: true),
        [ErrorKind.Forbidden] = new(StatusCodes.Status403Forbidden, "ErrorAccessDenied", "accessDenied", CodeIsFixed: false),
        [ErrorKind.SyncStateGone] = new(StatusCodes.Status410Gone, "syncStateNotFound", null, CodeIsFixed: true),
        [ErrorKind.PayloadTooLarge] = new(StatusCodes.Status413PayloadTooLarge, "requestEntityTooLarge", null, CodeIsFixed: false),
        [ErrorKind.Throttled] = new(StatusCodes.Status429TooManyRequests, "TooManyRequests", null, CodeIsFixed: true, HasRetryAfter: true),
        [ErrorKind.BackendUnavailable] = new(StatusCodes.Status503ServiceUnavailable, "serviceNotAvailable", null, CodeIsFixed: true, HasRetryAfter: true),
        [ErrorKind.BackendAuthFailed] = new(StatusCodes.Status401Unauthorized, "InvalidAuthenticationToken", null, CodeIsFixed: true),
        [ErrorKind.PreconditionFailed] = new(StatusCodes.Status412PreconditionFailed, "preconditionFailed", null, CodeIsFixed: false),
    };

    public static GraphErrorDescriptor Map(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var mapping = Mappings.TryGetValue(error.Kind, out var found) ? found : Mappings[ErrorKind.Unknown];

        var code = mapping.CodeIsFixed
            || string.IsNullOrWhiteSpace(error.Code)
            || string.Equals(error.Code, mapping.GenericCode, StringComparison.Ordinal)
                ? mapping.CanonicalCode
                : error.Code;

        // An unknown failure is a bug or an untranslated backend fault: say nothing about internals.
        var message = error.Kind == ErrorKind.Unknown || string.IsNullOrWhiteSpace(error.Message)
            ? DefaultMessage(error.Kind)
            : error.Message;

        int? retryAfter = mapping.HasRetryAfter ? Math.Max(1, error.RetryAfterSeconds ?? DefaultRetryAfterSeconds) : null;
        return new GraphErrorDescriptor(mapping.Status, code, message, retryAfter);
    }

    /// <summary>The status for a kind; exposed for tests and for callers that only need the number.</summary>
    public static int StatusFor(ErrorKind kind) =>
        Mappings.TryGetValue(kind, out var found) ? found.Status : StatusCodes.Status500InternalServerError;

    private static string DefaultMessage(ErrorKind kind) => kind switch
    {
        ErrorKind.Unknown => "An unexpected error occurred.",
        ErrorKind.NotFound => "The specified object was not found in the store.",
        ErrorKind.Unsupported => "This operation is not supported.",
        _ => "The request could not be completed.",
    };
}

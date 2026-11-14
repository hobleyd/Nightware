using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

using Nightware.Domain.Kernel;

namespace Nightware.Api.Graph.Errors;

/// <summary>
/// Turns unhandled exceptions into a Graph 500 envelope with no internals, and
/// gives bodiless framework errors (405, 415, routing/binding 400s) the same envelope.
/// </summary>
public sealed partial class GraphExceptionMiddleware(RequestDelegate next, ILogger<GraphExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away; there is no one to answer.
            return;
        }
        catch (BadHttpRequestException ex) when (!context.Response.HasStarted)
        {
            LogBadRequest(logger, ex.StatusCode, ex.Message);
            context.Response.Clear();
            await GraphErrorWriter.WriteAsync(context, new GraphErrorDescriptor(
                ex.StatusCode,
                ex.StatusCode == StatusCodes.Status400BadRequest ? "BadRequest" : CodeForStatus(ex.StatusCode),
                ex.Message,
                null)).ConfigureAwait(false);
            return;
        }
#pragma warning disable CA1031 // The last line of defence must catch everything.
        catch (Exception ex) when (!context.Response.HasStarted)
#pragma warning restore CA1031
        {
            LogUnhandled(logger, ex, context.Request.Method, context.Request.Path);
            context.Response.Clear();
            await GraphErrorWriter.WriteAsync(context, new Error(ErrorKind.Unknown, "generalException", string.Empty)).ConfigureAwait(false);
            return;
        }

        if (ShouldWrapBodilessError(context))
        {
            await GraphErrorWriter.WriteAsync(context, new GraphErrorDescriptor(
                context.Response.StatusCode,
                CodeForStatus(context.Response.StatusCode),
                MessageForStatus(context.Response.StatusCode),
                null)).ConfigureAwait(false);
        }
    }

    private static bool ShouldWrapBodilessError(HttpContext context) =>
        !context.Response.HasStarted
        && context.Response.StatusCode >= 400
        && context.Response.ContentLength is null or 0
        && string.IsNullOrEmpty(context.Response.ContentType)
        && GraphPaths.IsGraphRequest(context.Request.Path);

    internal static string CodeForStatus(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "BadRequest",
        StatusCodes.Status401Unauthorized => "InvalidAuthenticationToken",
        StatusCodes.Status403Forbidden => "ErrorAccessDenied",
        StatusCodes.Status404NotFound => "ResourceNotFound",
        StatusCodes.Status405MethodNotAllowed => "Request_BadRequest",
        StatusCodes.Status413PayloadTooLarge => "requestEntityTooLarge",
        StatusCodes.Status415UnsupportedMediaType => "UnsupportedMediaType",
        _ when status >= 500 => "generalException",
        _ => "BadRequest",
    };

    private static string MessageForStatus(int status) => status switch
    {
        StatusCodes.Status401Unauthorized => "Access token is empty.",
        StatusCodes.Status403Forbidden => "Access is denied. Check credentials and try again.",
        StatusCodes.Status404NotFound => "Resource not found.",
        StatusCodes.Status405MethodNotAllowed => "The HTTP method is not allowed for this resource.",
        StatusCodes.Status415UnsupportedMediaType => "The request content type is not supported.",
        _ when status >= 500 => "An unexpected error occurred.",
        _ => "The request is invalid.",
    };

    [LoggerMessage(Level = LogLevel.Error, Message = "Unhandled exception for {Method} {Path}")]
    private static partial void LogUnhandled(ILogger logger, Exception exception, string method, PathString path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Bad request ({Status}): {Reason}")]
    private static partial void LogBadRequest(ILogger logger, int status, string reason);
}

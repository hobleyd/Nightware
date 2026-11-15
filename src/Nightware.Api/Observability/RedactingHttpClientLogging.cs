using System.Diagnostics;

using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;

namespace Nightware.Api.Observability;

/// <summary>
/// HttpClient's request logging for every named client, in place of the factory's default (removed in
/// <see cref="LogRedaction.AddLogRedaction"/>), with the URL passed through <see cref="UrlRedaction"/>. The default
/// redacts only the query, and Nextcloud's public-share WebDAV carries the share token in the path.
/// </summary>
/// <remarks>
/// Same categories (<c>System.Net.Http.HttpClient.{name}.LogicalHandler</c> around the whole pipeline,
/// <c>….ClientHandler</c> around each attempt), levels and messages as the default, so
/// <c>Logging:LogLevel:System.Net.Http.HttpClient</c> keeps governing it. Header logging (Trace) is dropped: it
/// only ever printed <c>*</c> for every value.
/// </remarks>
internal sealed class RedactingHttpClientLoggingFilter(ILoggerFactory loggers) : IHttpMessageHandlerBuilderFilter
{
    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return builder =>
        {
            next(builder);
            var name = string.IsNullOrEmpty(builder.Name) ? "Default" : builder.Name;
            builder.AdditionalHandlers.Insert(0, new RedactingLoggingHandler(loggers.CreateLogger($"System.Net.Http.HttpClient.{name}.LogicalHandler"), outer: true));
            builder.AdditionalHandlers.Add(new RedactingLoggingHandler(loggers.CreateLogger($"System.Net.Http.HttpClient.{name}.ClientHandler"), outer: false));
        };
    }
}

internal sealed partial class RedactingLoggingHandler(ILogger logger, bool outer) : DelegatingHandler
{
    private static readonly Func<ILogger, HttpMethod, string, IDisposable?> RequestScope =
        LoggerMessage.DefineScope<HttpMethod, string>("HTTP {HttpMethod} {Uri}");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!logger.IsEnabled(LogLevel.Information))
        {
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var uri = UrlRedaction.Redact(request.RequestUri);
        using var scope = outer ? RequestScope(logger, request.Method, uri) : null;
        if (outer)
        {
            Log.PipelineStart(logger, request.Method, uri);
        }
        else
        {
            ClientLog.RequestStart(logger, request.Method, uri);
        }

        var started = Stopwatch.GetTimestamp();
        try
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (outer)
            {
                Log.PipelineEnd(logger, elapsed, (int)response.StatusCode);
            }
            else
            {
                ClientLog.RequestEnd(logger, elapsed, (int)response.StatusCode);
            }

            return response;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or TimeoutException)
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Log.Failed(logger, elapsed, e);
            throw;
        }
    }

    /// <summary>The <c>LogicalHandler</c>'s messages (and a failure in either), with the default's event ids.</summary>
    private static partial class Log
    {
        [LoggerMessage(EventId = 100, EventName = "RequestPipelineStart", Level = LogLevel.Information, Message = "Start processing HTTP request {HttpMethod} {Uri}")]
        public static partial void PipelineStart(ILogger logger, HttpMethod httpMethod, string uri);

        [LoggerMessage(EventId = 101, EventName = "RequestPipelineEnd", Level = LogLevel.Information, Message = "End processing HTTP request after {ElapsedMilliseconds}ms - {StatusCode}")]
        public static partial void PipelineEnd(ILogger logger, double elapsedMilliseconds, int statusCode);

        [LoggerMessage(EventId = 104, EventName = "RequestFailed", Level = LogLevel.Information, Message = "HTTP request failed after {ElapsedMilliseconds}ms")]
        public static partial void Failed(ILogger logger, double elapsedMilliseconds, Exception exception);
    }

    /// <summary>The <c>ClientHandler</c>'s messages, with the default's event ids.</summary>
    private static partial class ClientLog
    {
        [LoggerMessage(EventId = 100, EventName = "RequestStart", Level = LogLevel.Information, Message = "Sending HTTP request {HttpMethod} {Uri}")]
        public static partial void RequestStart(ILogger logger, HttpMethod httpMethod, string uri);

        [LoggerMessage(EventId = 101, EventName = "RequestEnd", Level = LogLevel.Information, Message = "Received HTTP response headers after {ElapsedMilliseconds}ms - {StatusCode}")]
        public static partial void RequestEnd(ILogger logger, double elapsedMilliseconds, int statusCode);
    }
}

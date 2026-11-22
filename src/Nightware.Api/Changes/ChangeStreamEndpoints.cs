using System.Globalization;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

using Nightware.Api.Auth;
using Nightware.Api.Graph.Errors;
using Nightware.Api.Hosting;
using Nightware.Application.Changes;
using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Changes;

/// <summary>
/// <c>GET /nightware/v1/changes[?mailbox={address}]</c> (ADR 0007): a Server-Sent Events stream that tells
/// NightMail when a mailbox's mail or calendar changed, so it syncs then instead of on a timer. It is not Graph
/// surface: it lives outside <c>/v1.0</c>, is advertised by discovery's <c>changesUrl</c>, and carries no item data,
/// only which areas moved; the client fetches the changes over its usual delta routes.
/// </summary>
/// <remarks>
/// Events, each <c>event:</c> + one JSON <c>data:</c> line:
/// <list type="bullet">
/// <item><c>ready</c> <c>{"areas":["mail","calendar"]}</c>: the baseline is read; changes after this are signalled.</item>
/// <item><c>changed</c> <c>{"areas":["mail"]}</c>: those areas moved since the last round.</item>
/// <item><c>end</c> <c>{"reason":"expired"|"credentials"}</c>: the stream is closing; reconnect (with a fresh token).</item>
/// </list>
/// Comment lines (<c>: keepalive</c>) keep idle connections open. Errors before the stream opens are the usual
/// Graph error envelope: 401 for a bad token, 404 when the mailbox has nothing to watch or is not the caller's to
/// open, 429 with <c>Retry-After</c> past <see cref="ChangeStreamOptions.MaxStreamsPerUser"/>.
/// </remarks>
public static class ChangeStreamEndpoints
{
    public const string Path = "/nightware/v1/changes";

    /// <summary>How long before the access token's expiry a stream ends, so its last reads still carry a valid token.</summary>
    public static readonly TimeSpan TokenExpirySkew = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Maps the stream when <see cref="ChangeStreamOptions.Enabled"/>.</summary>
    public static IEndpointRouteBuilder MapChangeStream(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (endpoints.ServiceProvider.GetRequiredService<IOptions<ChangeStreamOptions>>().Value.Enabled)
        {
            endpoints.MapGet(Path, HandleAsync)
                .RequireAuthorization()
                .RequireNightMailCors()
                .WithMetadata(LongLivedRequest.Instance)
                .ExcludeFromDescription();
        }

        return endpoints;
    }

    internal static async Task HandleAsync(
        HttpContext context,
        string? mailbox,
        IMailboxResolver resolver,
        WatchChanges watch,
        ChangeStreamLimiter limiter,
        IOptions<ChangeStreamOptions> configured,
        TimeProvider time)
    {
        var options = configured.Value;
        var aborted = context.RequestAborted;
        var target = await resolver.ResolveAsync(mailbox, aborted).ConfigureAwait(false);
        if (!target.IsSuccess)
        {
            await GraphErrorWriter.WriteAsync(context, target.Error).ConfigureAwait(false);
            return;
        }

        var user = context.User.FindFirst(HttpUserContext.SubjectClaim)?.Value ?? string.Empty;
        using var lease = limiter.TryAcquire(user);
        if (lease is null)
        {
            var retryAfter = (int)Math.Ceiling(options.PollInterval.TotalSeconds);
            await GraphErrorWriter.WriteAsync(context, new Error(
                ErrorKind.Throttled,
                "TooManyRequests",
                string.Create(CultureInfo.InvariantCulture, $"At most {options.MaxStreamsPerUser} change streams may be open at once."))
            {
                RetryAfterSeconds = retryAfter,
            }).ConfigureAwait(false);
            return;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        deadline.CancelAfter(Lifetime(context, options, time.GetUtcNow()));

        var started = false;
        var lastWrite = time.GetUtcNow();
        try
        {
            await foreach (var item in watch.ExecuteAsync(target.Value, options.PollInterval, deadline.Token).ConfigureAwait(false))
            {
                switch (item)
                {
                    case WatchEvent.Started { Areas.Count: 0 }:
                        await GraphErrorWriter.WriteAsync(context, Error.NotFound("This mailbox has nothing to watch.")).ConfigureAwait(false);
                        return;
                    case WatchEvent.Started s:
                        Open(context);
                        started = true;
                        await WriteAsync(context, "retry: 10000\n\n", aborted).ConfigureAwait(false);
                        await WriteEventAsync(context, "ready", new { areas = Names(s.Areas) }, aborted).ConfigureAwait(false);
                        lastWrite = time.GetUtcNow();
                        break;
                    case WatchEvent.Round { Result.Changed.Count: > 0 } r:
                        await WriteEventAsync(context, "changed", new { areas = Names(r.Result.Changed) }, aborted).ConfigureAwait(false);
                        lastWrite = time.GetUtcNow();
                        break;
                    case WatchEvent.Round when time.GetUtcNow() - lastWrite >= options.KeepAlive:
                        await WriteAsync(context, ": keepalive\n\n", aborted).ConfigureAwait(false);
                        lastWrite = time.GetUtcNow();
                        break;
                    case WatchEvent.Ended e when !started:
                        await GraphErrorWriter.WriteAsync(context, e.Reason).ConfigureAwait(false);
                        return;
                    case WatchEvent.Ended:
                        await WriteEventAsync(context, "end", new { reason = "credentials" }, aborted).ConfigureAwait(false);
                        return;
                }
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !aborted.IsCancellationRequested)
        {
            if (started)
            {
                await WriteEventAsync(context, "end", new { reason = "expired" }, aborted).ConfigureAwait(false);
            }
            else
            {
                await GraphErrorWriter.WriteAsync(context, Error.BackendUnavailable("The change stream could not start in time.", 5)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (aborted.IsCancellationRequested)
        {
            // The client went away; there is nobody to answer.
        }
    }

    /// <summary>
    /// How long the stream may run: <see cref="ChangeStreamOptions.MaxDuration"/>, cut to just before the access
    /// token's <c>exp</c> so the reads made on the caller's behalf never outlive it. At least one second.
    /// </summary>
    public static TimeSpan Lifetime(HttpContext context, ChangeStreamOptions options, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        var lifetime = options.MaxDuration;
        if (long.TryParse(context.User.FindFirst("exp")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var exp))
        {
            var untilExpiry = DateTimeOffset.FromUnixTimeSeconds(exp) - TokenExpirySkew - now;
            if (untilExpiry < lifetime)
            {
                lifetime = untilExpiry;
            }
        }

        return lifetime < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : lifetime;
    }

    private static string[] Names(IEnumerable<ChangeArea> areas) =>
        [.. areas.Select(static a => a.ToString().ToLowerInvariant())];

    private static void Open(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers[HeaderNames.CacheControl] = "no-cache, no-store";
        // nginx would otherwise hold events back in its proxy buffer.
        context.Response.Headers["X-Accel-Buffering"] = "no";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
    }

    private static Task WriteEventAsync(HttpContext context, string name, object data, CancellationToken cancellationToken) =>
        WriteAsync(context, $"event: {name}\ndata: {JsonSerializer.Serialize(data, Json)}\n\n", cancellationToken);

    private static async Task WriteAsync(HttpContext context, string text, CancellationToken cancellationToken)
    {
        await context.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), cancellationToken).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}

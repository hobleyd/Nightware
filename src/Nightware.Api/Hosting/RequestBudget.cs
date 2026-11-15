using System.Globalization;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Nightware.Api.Graph.Errors;
using Nightware.Api.Observability;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Hosting;

/// <summary>How long one request may take end to end (configuration section <c>RequestTimeouts</c>).</summary>
public sealed class RequestBudgetOptions
{
    public const string SectionName = "RequestTimeouts";

    /// <summary>
    /// Budget for an ordinary call. NightMail gives up after a 30 s receive timeout, so the façade
    /// answers first, with a response the client knows how to handle, instead of leaving it to a
    /// transport error.
    /// </summary>
    public TimeSpan Default { get; set; } = TimeSpan.FromSeconds(25);

    /// <summary>
    /// Budget for moving bytes: attachment and <c>$value</c>/<c>content</c> routes, uploads, and any
    /// request whose body is larger than <see cref="TransferBodyThresholdBytes"/>.
    /// </summary>
    public TimeSpan Transfer { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>A body larger than this gets the <see cref="Transfer"/> budget whatever the route.</summary>
    public long TransferBodyThresholdBytes { get; set; } = 1_000_000;
}

internal sealed class RequestBudgetOptionsValidator : IValidateOptions<RequestBudgetOptions>
{
    public ValidateOptionsResult Validate(string? name, RequestBudgetOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var failures = new List<string>();
        if (options.Default <= TimeSpan.Zero)
        {
            failures.Add("RequestTimeouts:Default must be positive.");
        }

        if (options.Transfer < options.Default)
        {
            failures.Add("RequestTimeouts:Transfer must be at least RequestTimeouts:Default.");
        }

        if (options.TransferBodyThresholdBytes < 0)
        {
            failures.Add("RequestTimeouts:TransferBodyThresholdBytes cannot be negative.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>
/// Gives every request a time budget and answers a request that overruns it with a Graph error
/// instead of letting it hang until the client's own timeout.
/// </summary>
/// <remarks>
/// <para>The budget cancels the request's <see cref="HttpContext.RequestAborted"/>; adapters pass
/// that token to every backend call and rethrow its cancellation, which lands here.</para>
/// <para>The answer depends on whether the request is safe to repeat, because NightMail's
/// <c>RetryInterceptor</c> retries <b>any</b> method on 503. A read that overruns gets
/// <c>503 serviceNotAvailable</c> with an integer <c>Retry-After</c> (retried, harmless). A write
/// that overruns may already have taken effect on the backend — a sendMail that Stalwart accepted
/// a moment too late — so it gets <c>504 gatewayTimeout</c>, which the client surfaces instead of
/// replaying.</para>
/// <para>ASP.NET Core's <c>RequestTimeouts</c> middleware is not used: it picks a policy only from
/// endpoint metadata (this needs the body size too), fixes the status per policy, and is disabled
/// under a debugger.</para>
/// </remarks>
public sealed partial class RequestBudgetMiddleware(
    RequestDelegate next,
    IOptions<RequestBudgetOptions> options,
    NightwareMetrics metrics,
    ILogger<RequestBudgetMiddleware> logger)
{
    public const string GatewayTimeoutCode = "gatewayTimeout";

    private readonly RequestBudgetOptions _options = options.Value;

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var budget = BudgetFor(context, _options);
        var original = context.RequestAborted;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(original);
        timeout.CancelAfter(budget);
        context.RequestAborted = timeout.Token;
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !original.IsCancellationRequested)
        {
            // The error is written on the connection's own token: the expired budget token would
            // cancel the write itself.
            context.RequestAborted = original;
            await OnTimeoutAsync(context, budget).ConfigureAwait(false);
        }
        finally
        {
            context.RequestAborted = original;
        }
    }

    /// <summary>The budget for this request: transfer-shaped routes and large bodies get the long one.</summary>
    public static TimeSpan BudgetFor(HttpContext context, RequestBudgetOptions options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
        var transfer = (route is not null && TransferRoute().IsMatch(route))
            || context.Request.ContentLength > options.TransferBodyThresholdBytes;
        return transfer ? options.Transfer : options.Default;
    }

    private async Task OnTimeoutAsync(HttpContext context, TimeSpan budget)
    {
        var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(unmatched)";
        var safe = IsSafe(context.Request.Method);
        metrics.RequestTimedOut(context.Request.Method, route);
        LogTimeout(logger, context.Request.Method, route, budget.TotalSeconds);

        if (context.Response.HasStarted)
        {
            // Part of the body is on the wire; the only honest signal left is a broken connection.
            context.Abort();
            return;
        }

        context.Response.Clear();
        var seconds = string.Create(CultureInfo.InvariantCulture, $"{budget.TotalSeconds:0.#}");
        if (safe)
        {
            await GraphErrorWriter.WriteAsync(context, Error.BackendUnavailable(
                $"The request did not complete within {seconds} seconds. Try again later.")).ConfigureAwait(false);
        }
        else
        {
            await GraphErrorWriter.WriteAsync(context, new GraphErrorDescriptor(
                StatusCodes.Status504GatewayTimeout,
                GatewayTimeoutCode,
                $"The request did not complete within {seconds} seconds. It may or may not have taken effect.",
                null)).ConfigureAwait(false);
        }
    }

    internal static bool IsSafe(string method) =>
        HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);

    // Raw bytes in or out: attachments, $value/content streams, upload sessions, sendMail with inline attachments.
    [GeneratedRegex(@"(/attachments\b|/\$value$|/content$|uploadSession|/sendMail$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TransferRoute();

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Method} {Route} exceeded its {BudgetSeconds}s request budget")]
    private static partial void LogTimeout(ILogger logger, string method, string route, double budgetSeconds);
}

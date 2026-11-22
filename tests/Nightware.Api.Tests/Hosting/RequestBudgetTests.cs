using System.Diagnostics.Metrics;
using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

using Nightware.Api.Hosting;
using Nightware.Api.Observability;
using Nightware.Api.Tests.Support;

namespace Nightware.Api.Tests.Hosting;

/// <summary>The per-request budget: reads overrunning it get a retryable 503, writes a final 504.</summary>
public sealed class RequestBudgetTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Dictionary<string, string?> ShortBudget = new()
    {
        ["RequestTimeouts:Default"] = "00:00:00.200",
        ["RequestTimeouts:Transfer"] = "00:00:30",
        ["RequestTimeouts:TransferBodyThresholdBytes"] = "1000",
    };

    // Waits on the request token the way an adapter waits on a backend call.
    private static async Task<IResult> Hang(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return Results.Ok();
    }

    private static async Task<IResult> Slow(CancellationToken cancellationToken)
    {
        // Far past the 0.2 s default budget: under CI load the budget timer's callback can run late,
        // and with a narrow margin the delay finished first and the request came back 200.
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        return Results.Ok(new { done = true });
    }

    private static Task<TestHost> StartAsync(Action<IServiceCollection>? services = null, Dictionary<string, string?>? configuration = null) =>
        TestHost.StartAsync(
            s =>
            {
                s.AddGraphProbes(static g =>
                {
                    g.MapGet("/hang", Hang);
                    g.MapPost("/hang", Hang);
                    g.MapDelete("/hang", Hang);
                    g.MapGet("/probe/messages/{id}/attachments/{attachmentId}/$value", Slow);
                    g.MapGet("/probe/messages/{id}/$value", Slow);
                    g.MapPost("/probe/sendMail", Slow);
                    g.MapPost("/slow", Slow);
                });
                services?.Invoke(s);
            },
            configuration: configuration ?? ShortBudget);

    [Fact]
    public async Task A_read_over_budget_is_a_graph_503_with_integer_retry_after()
    {
        await using var host = await StartAsync();

        var response = await host.AuthorizedClient().GetAsync("/v1.0/hang", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        response.Headers.GetValues("Retry-After").Single().ShouldBe("5");
        var body = await response.ReadObjectAsync();
        body.Str("error.code").ShouldBe("serviceNotAvailable");
        body.Str("error.message")!.ShouldContain("did not complete within 0.2 seconds");
        body.Str("error.innerError.request-id").ShouldBe(response.Headers.GetValues("request-id").Single());
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("DELETE")]
    public async Task A_write_over_budget_is_a_504_the_client_does_not_replay(string method)
    {
        await using var host = await StartAsync();

        var response = await host.AuthorizedClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), "/v1.0/hang"), Ct);

        // NightMail's RetryInterceptor retries 429/503 for every method; 504 is surfaced instead.
        response.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
        response.Headers.Contains("Retry-After").ShouldBeFalse();
        var body = await response.ReadObjectAsync();
        body.Str("error.code").ShouldBe(RequestBudgetMiddleware.GatewayTimeoutCode);
        body.Str("error.message")!.ShouldContain("may or may not have taken effect");
    }

    [Theory]
    [InlineData("/v1.0/probe/messages/m1/attachments/a1/$value")]
    [InlineData("/v1.0/probe/messages/m1/$value")]
    public async Task Transfer_routes_get_the_long_budget(string path)
    {
        await using var host = await StartAsync();

        var response = await host.AuthorizedClient().GetAsync(path, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SendMail_gets_the_long_budget()
    {
        await using var host = await StartAsync();

        var response = await host.AuthorizedClient().PostAsync("/v1.0/probe/sendMail", new StringContent("{}"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_large_body_gets_the_long_budget_on_any_route()
    {
        await using var host = await StartAsync();
        var client = host.AuthorizedClient();

        var small = await client.PostAsync("/v1.0/slow", new StringContent("{}"), Ct);
        var large = await client.PostAsync("/v1.0/slow", new StringContent(new string('x', 5000)), Ct);

        small.StatusCode.ShouldBe(HttpStatusCode.GatewayTimeout);
        large.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_timeout_is_counted_by_route_template()
    {
        using var timeouts = new TimeoutCounter();
        await using var host = await StartAsync();

        await host.AuthorizedClient().GetAsync("/v1.0/hang", Ct);

        timeouts.Snapshot().ShouldContain(static m => m.Route == "/v1.0/hang" && m.Method == "GET");
    }

    [Fact]
    public async Task A_client_that_goes_away_is_not_a_timeout()
    {
        using var timeouts = new TimeoutCounter();
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await StartAsync(s => s.AddGraphProbes(g => g.MapGet("/abandoned", async (CancellationToken token) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            finally
            {
                observed.TrySetResult();
            }

            return Results.Ok();
        })), new Dictionary<string, string?> { ["RequestTimeouts:Default"] = "00:01:00" });
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        abort.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Should.ThrowAsync<OperationCanceledException>(() => host.AuthorizedClient().GetAsync("/v1.0/abandoned", abort.Token));
        await observed.Task.WaitAsync(TimeSpan.FromMinutes(2), Ct);

        // The listener is process-wide; other tests' timeouts may show up, never this route's.
        timeouts.Snapshot().ShouldNotContain(static m => m.Route == "/v1.0/abandoned");
    }

    /// <summary>Collects <c>nightware.request.timeouts</c> measurements.</summary>
    private sealed class TimeoutCounter : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<(string? Route, string? Method)> _measurements = [];

        public TimeoutCounter()
        {
            _listener.InstrumentPublished = static (instrument, l) =>
            {
                if (instrument is { Name: "nightware.request.timeouts", Meter.Name: NightwareMetrics.MeterName })
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                var map = tags.ToArray().ToDictionary(static t => t.Key, static t => t.Value as string);
                lock (_measurements)
                {
                    _measurements.Add((map.GetValueOrDefault("http.route"), map.GetValueOrDefault("http.request.method")));
                }
            });
            _listener.Start();
        }

        public List<(string? Route, string? Method)> Snapshot()
        {
            lock (_measurements)
            {
                return [.. _measurements];
            }
        }

        public void Dispose() => _listener.Dispose();
    }

    [Fact]
    public void Budget_selection_uses_the_route_template_and_the_body_size()
    {
        var options = new RequestBudgetOptions { Default = TimeSpan.FromSeconds(1), Transfer = TimeSpan.FromSeconds(9), TransferBodyThresholdBytes = 10 };

        RequestBudgetMiddleware.BudgetFor(Context("/v1.0/me/messages/{id}"), options).ShouldBe(TimeSpan.FromSeconds(1));
        RequestBudgetMiddleware.BudgetFor(Context("/v1.0/me/messages/{id}/attachments"), options).ShouldBe(TimeSpan.FromSeconds(9));
        RequestBudgetMiddleware.BudgetFor(Context("/v1.0/shares/{id}/driveItem/content"), options).ShouldBe(TimeSpan.FromSeconds(9));
        RequestBudgetMiddleware.BudgetFor(Context("/v1.0/me/todo/lists/{l}/tasks/{t}/attachments/createUploadSession"), options).ShouldBe(TimeSpan.FromSeconds(9));
        RequestBudgetMiddleware.BudgetFor(Context("/v1.0/me/messages/{id}", contentLength: 11), options).ShouldBe(TimeSpan.FromSeconds(9));
        RequestBudgetMiddleware.BudgetFor(Context(null), options).ShouldBe(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Every_mapped_content_route_gets_the_transfer_budget()
    {
        await using var host = await TestHost.StartAsync();
        var options = new RequestBudgetOptions { Default = TimeSpan.FromSeconds(1), Transfer = TimeSpan.FromSeconds(9) };
        var routes = host.App.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(static e => e.RoutePattern.RawText!)
            .Where(static r => r.EndsWith("/$value", StringComparison.Ordinal) || r.Contains("/attachments", StringComparison.Ordinal))
            .ToList();

        routes.ShouldNotBeEmpty("the mail feature maps $value and attachment routes");
        routes.ShouldAllBe(r => RequestBudgetMiddleware.BudgetFor(Context(r, null), options) == TimeSpan.FromSeconds(9));
    }

    [Theory]
    [InlineData("GET", true)]
    [InlineData("HEAD", true)]
    [InlineData("OPTIONS", true)]
    [InlineData("POST", false)]
    [InlineData("PATCH", false)]
    [InlineData("PUT", false)]
    [InlineData("DELETE", false)]
    public void Only_safe_methods_are_answered_with_a_retryable_status(string method, bool safe) =>
        RequestBudgetMiddleware.IsSafe(method).ShouldBe(safe);

    [Theory]
    [InlineData(0, 10, 0, "Default")]
    [InlineData(10, 5, 0, "Transfer")]
    [InlineData(10, 20, -1, "TransferBodyThresholdBytes")]
    public void Invalid_budgets_are_rejected(int defaultSeconds, int transferSeconds, long threshold, string named)
    {
        var result = new RequestBudgetOptionsValidator().Validate(null, new RequestBudgetOptions
        {
            Default = TimeSpan.FromSeconds(defaultSeconds),
            Transfer = TimeSpan.FromSeconds(transferSeconds),
            TransferBodyThresholdBytes = threshold,
        });

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain(named);
    }

    [Fact]
    public void The_default_budget_answers_inside_the_clients_30_second_receive_timeout()
    {
        new RequestBudgetOptions().Default.ShouldBeLessThan(TimeSpan.FromSeconds(30));
        new RequestBudgetOptionsValidator().Validate(null, new RequestBudgetOptions()).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void A_long_lived_route_has_no_budget()
    {
        var context = new DefaultHttpContext();
        context.SetEndpoint(new RouteEndpoint(
            static _ => Task.CompletedTask,
            Microsoft.AspNetCore.Routing.Patterns.RoutePatternFactory.Parse("/nightware/v1/changes"),
            0,
            new EndpointMetadataCollection(LongLivedRequest.Instance),
            "changes"));

        RequestBudgetMiddleware.BudgetFor(context, new RequestBudgetOptions()).ShouldBe(Timeout.InfiniteTimeSpan);
    }

    private static DefaultHttpContext Context(string? routeTemplate, long? contentLength = null)
    {
        var context = new DefaultHttpContext();
        context.Request.ContentLength = contentLength;
        if (routeTemplate is not null)
        {
            context.SetEndpoint(new RouteEndpoint(
                static _ => Task.CompletedTask,
                Microsoft.AspNetCore.Routing.Patterns.RoutePatternFactory.Parse(routeTemplate),
                0,
                EndpointMetadataCollection.Empty,
                routeTemplate));
        }

        return context;
    }
}

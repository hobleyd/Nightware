using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

using Nightware.Api.Discovery;
using Nightware.Api.Tests.Support;
using Nightware.Api.Throttling;

namespace Nightware.Api.Tests.Throttling;

/// <summary>The per-user limiter end to end: 429 shape, partitions and exemptions.</summary>
public sealed class GraphRateLimitingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<TestHost> StartAsync(Dictionary<string, string?> limits, Action<Microsoft.AspNetCore.Routing.RouteGroupBuilder>? probes = null) =>
        TestHost.StartAsync(
            services => services.AddGraphProbes(probes ?? (static g => g.MapGet("/probe", static () => Results.Ok(new { ok = true })))),
            configuration: limits);

    private static Dictionary<string, string?> Bucket(int tokens, int concurrency = 0) => new()
    {
        ["RateLimiting:TokenLimit"] = tokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["RateLimiting:TokensPerPeriod"] = "1",
        ["RateLimiting:ReplenishmentPeriod"] = "00:01:00",
        ["RateLimiting:ConcurrentRequestLimit"] = concurrency.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    [Fact]
    public async Task A_user_over_the_burst_gets_graph_429_with_integer_retry_after()
    {
        await using var host = await StartAsync(Bucket(2));
        var client = host.AuthorizedClient();
        client.DefaultRequestHeaders.Add("client-request-id", "nightmail-123");

        (await client.GetAsync("/v1.0/probe", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/v1.0/probe", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var response = await client.GetAsync("/v1.0/probe", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        var retryAfter = response.Headers.GetValues("Retry-After").Single();
        int.TryParse(retryAfter, out var seconds).ShouldBeTrue($"Retry-After '{retryAfter}' must be integer seconds");
        seconds.ShouldBeInRange(1, 60);
        response.Headers.GetValues("request-id").ShouldHaveSingleItem();
        response.Headers.GetValues("client-request-id").Single().ShouldBe("nightmail-123");
        var body = await response.ReadObjectAsync();
        body.Str("error.code").ShouldBe("TooManyRequests");
        body.Str("error.message")!.ShouldContain("Too many requests");
        body.Str("error.innerError.client-request-id").ShouldBe("nightmail-123");
    }

    [Fact]
    public async Task Users_are_limited_independently()
    {
        await using var host = await StartAsync(Bucket(1));
        var alice = host.AuthorizedClient(TestHost.Token(sub: "alice"));
        var bob = host.AuthorizedClient(TestHost.Token(sub: "bob"));

        (await alice.GetAsync("/v1.0/probe", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await alice.GetAsync("/v1.0/probe", Ct)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        (await bob.GetAsync("/v1.0/probe", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Both_graph_versions_share_one_bucket()
    {
        await using var host = await StartAsync(Bucket(1));
        var client = host.AuthorizedClient();

        (await client.GetAsync("/v1.0/probe", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/beta/probe", Ct)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Unauthenticated_callers_cannot_drain_a_users_bucket()
    {
        await using var host = await StartAsync(Bucket(1));

        // Garbage-token traffic is partitioned by address: it exhausts its own bucket only.
        (await host.Client.GetAsync("/v1.0/probe", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await host.Client.GetAsync("/v1.0/probe", Ct)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        (await host.AuthorizedClient().GetAsync("/v1.0/probe", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Discovery_and_health_are_never_limited()
    {
        await using var host = await StartAsync(Bucket(1));

        for (var i = 0; i < 5; i++)
        {
            (await host.Client.GetAsync(DiscoveryEndpoints.Path, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
            (await host.Client.GetAsync("/healthz", Ct)).StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests);
        }
    }

    [Fact]
    public async Task Too_many_requests_in_flight_are_rejected_with_retry_after_one_second()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await StartAsync(Bucket(100, concurrency: 1), g => g.MapGet("/slow", async () =>
        {
            entered.TrySetResult();
            await gate.Task;
            return Results.Ok();
        }));
        var client = host.AuthorizedClient();

        var first = client.GetAsync("/v1.0/slow", Ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var second = await client.GetAsync("/v1.0/slow", Ct);
        gate.SetResult();

        second.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        second.Headers.GetValues("Retry-After").Single().ShouldBe("1");
        (await first).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_disabled_limiter_admits_everything()
    {
        var config = Bucket(1);
        config["RateLimiting:Enabled"] = "false";
        await using var host = await StartAsync(config);
        var client = host.AuthorizedClient();

        for (var i = 0; i < 5; i++)
        {
            (await client.GetAsync("/v1.0/probe", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task The_defaults_do_not_throttle_a_start_up_burst()
    {
        await using var host = await StartAsync([]);
        var client = host.AuthorizedClient();
        using var inFlight = new SemaphoreSlim(24);

        // A cold start on three devices at once: folders, a page of mail, 20 body prefetches,
        // sender photos, calendarView, task lists, contacts — about 150 calls, a couple of dozen in flight.
        var responses = await Task.WhenAll(Enumerable.Range(0, 150).Select(async _ =>
        {
            await inFlight.WaitAsync(Ct);
            try
            {
                return await client.GetAsync("/v1.0/probe", Ct);
            }
            finally
            {
                inFlight.Release();
            }
        }));

        responses.ShouldAllBe(static r => r.StatusCode == HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("GET", "/v1.0/me/messages", true, "user:u1")]
    [InlineData("GET", "/beta/me/messages", true, "user:u1")]
    [InlineData("GET", "/v1.0/me/messages", false, "ip:10.1.2.3")]
    [InlineData("OPTIONS", "/v1.0/me/messages", true, null)]
    [InlineData("GET", "/.well-known/nightware", true, null)]
    [InlineData("GET", "/healthz", false, null)]
    public void Partition_key_is_the_subject_for_graph_calls_only(string method, string path, bool authenticated, string? expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = IPAddress.Parse("10.1.2.3");
        if (authenticated)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "u1")], "Bearer"));
        }

        GraphRateLimiting.PartitionKey(context).ShouldBe(expected);
    }

    [Fact]
    public async Task Retry_after_rounds_the_bucket_wait_up_to_whole_seconds()
    {
        using var limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = 1,
            TokensPerPeriod = 1,
            ReplenishmentPeriod = TimeSpan.FromMilliseconds(2500),
            QueueLimit = 0,
            AutoReplenishment = false,
        });
        using var granted = await limiter.AcquireAsync(1, Ct);
        using var rejected = await limiter.AcquireAsync(1, Ct);

        rejected.IsAcquired.ShouldBeFalse();
        GraphRateLimiting.RetryAfterSeconds(rejected).ShouldBe(3);
    }

    [Fact]
    public async Task A_lease_without_retry_metadata_waits_one_second()
    {
        using var limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions { PermitLimit = 1, QueueLimit = 0 });
        using var held = await limiter.AcquireAsync(1, Ct);
        using var rejected = await limiter.AcquireAsync(1, Ct);

        GraphRateLimiting.RetryAfterSeconds(rejected).ShouldBe(GraphRateLimiting.ConcurrencyRetryAfterSeconds);
    }

    [Theory]
    [InlineData(0, 1, 1, 0, "TokenLimit")]
    [InlineData(1, 0, 1, 0, "TokensPerPeriod")]
    [InlineData(1, 1, 0, 0, "ReplenishmentPeriod")]
    [InlineData(1, 1, 1, -1, "ConcurrentRequestLimit")]
    public void Invalid_limits_are_rejected(int tokens, int perPeriod, int periodSeconds, int concurrency, string named)
    {
        var options = new RateLimitingOptions
        {
            TokenLimit = tokens,
            TokensPerPeriod = perPeriod,
            ReplenishmentPeriod = TimeSpan.FromSeconds(periodSeconds),
            ConcurrentRequestLimit = concurrency,
        };

        var result = new RateLimitingOptionsValidator().Validate(null, options);

        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain(named);
    }

    [Fact]
    public void Limits_are_not_validated_while_disabled()
    {
        var result = new RateLimitingOptionsValidator().Validate(null, new RateLimitingOptions { Enabled = false, TokenLimit = 0 });

        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public async Task Invalid_limits_fail_the_host_at_start()
    {
        await Should.ThrowAsync<Microsoft.Extensions.Options.OptionsValidationException>(() =>
            TestHost.StartAsync(configuration: new Dictionary<string, string?> { ["RateLimiting:TokenLimit"] = "0" }));
    }
}

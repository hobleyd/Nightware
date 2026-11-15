using System.Net;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Time.Testing;

using Nightware.Api.Composition;
using Nightware.Api.Hosting;
using Nightware.Api.Tests.Support;

namespace Nightware.Api.Tests.Hosting;

/// <summary>
/// <c>/healthz</c> is anonymous and public: its backend fan-out runs at most once per
/// <see cref="ReadinessCache.Lifetime"/>, however often it is hit.
/// </summary>
public sealed class ReadinessCacheTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero));

    /// <summary>A host whose readiness report holds only <paramref name="check"/> (the backend checks are removed).</summary>
    private Task<TestHost> StartAsync(CountingCheck check) =>
        TestHost.StartAsync(s =>
        {
            s.AddSingleton<TimeProvider>(_clock);
            s.Configure<HealthCheckServiceOptions>(static o => o.Registrations.Clear());
            s.AddHealthChecks().AddCheck("counting", check);
        });

    [Fact]
    public async Task Repeated_hits_within_the_lifetime_run_the_checks_once()
    {
        var check = new CountingCheck(HealthStatus.Healthy);
        await using var host = await StartAsync(check);

        for (var i = 0; i < 5; i++)
        {
            (await host.Client.GetAsync(NightwareComposition.HealthPath, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        check.Runs.ShouldBe(1);
    }

    [Fact]
    public async Task The_checks_run_again_once_the_lifetime_has_passed()
    {
        var check = new CountingCheck(HealthStatus.Healthy);
        await using var host = await StartAsync(check);

        await host.Client.GetAsync(NightwareComposition.HealthPath, Ct);
        _clock.Advance(ReadinessCache.Lifetime + TimeSpan.FromSeconds(1));
        await host.Client.GetAsync(NightwareComposition.HealthPath, Ct);

        check.Runs.ShouldBe(2);
    }

    [Fact]
    public async Task Concurrent_hits_share_one_run()
    {
        var check = new CountingCheck(HealthStatus.Healthy) { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var host = await StartAsync(check);

        var requests = Enumerable.Range(0, 10).Select(_ => host.Client.GetAsync(NightwareComposition.HealthPath, Ct)).ToArray();
        await check.Started.Task.WaitAsync(Ct);
        check.Gate.SetResult();
        await Task.WhenAll(requests);

        check.Runs.ShouldBe(1);
    }

    [Fact]
    public async Task An_unhealthy_report_is_cached_too()
    {
        var check = new CountingCheck(HealthStatus.Unhealthy);
        await using var host = await StartAsync(check);

        var first = await host.Client.GetAsync(NightwareComposition.HealthPath, Ct);
        var second = await host.Client.GetAsync(NightwareComposition.HealthPath, Ct);

        first.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        second.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
        (await second.Content.ReadAsStringAsync(Ct)).ShouldBe("Unhealthy");
        second.Headers.CacheControl!.NoStore.ShouldBeTrue();
        check.Runs.ShouldBe(1);
    }

    [Fact]
    public async Task A_healthy_report_answers_200()
    {
        await using var host = await StartAsync(new CountingCheck(HealthStatus.Healthy));

        var response = await host.Client.GetAsync(NightwareComposition.HealthPath, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldBe("Healthy");
    }

    [Fact]
    public async Task Liveness_never_runs_the_checks()
    {
        var check = new CountingCheck(HealthStatus.Healthy);
        await using var host = await StartAsync(check);

        for (var i = 0; i < 3; i++)
        {
            (await host.Client.GetAsync(NightwareComposition.LivenessPath, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        check.Runs.ShouldBe(0);
    }

    [Fact]
    public async Task A_caller_that_gives_up_does_not_cancel_the_shared_run()
    {
        var check = new CountingCheck(HealthStatus.Healthy) { Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        await using var host = await StartAsync(check);
        var cache = host.App.Services.GetRequiredService<ReadinessCache>();

        using var abandoned = new CancellationTokenSource();
        var first = cache.GetAsync(abandoned.Token);
        await check.Started.Task.WaitAsync(Ct);
        await abandoned.CancelAsync();
        var second = cache.GetAsync(Ct);
        check.Gate.SetResult();

        await Should.ThrowAsync<OperationCanceledException>(() => first);
        (await second).Entries.ShouldContainKey("counting");
        check.Runs.ShouldBe(1);
    }

    private sealed class CountingCheck(HealthStatus status) : IHealthCheck
    {
        private int _runs;

        public int Runs => Volatile.Read(ref _runs);

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource? Gate { get; init; }

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _runs);
            Started.TrySetResult();
            if (Gate is not null)
            {
                await Gate.Task.WaitAsync(cancellationToken);
            }

            return new HealthCheckResult(status);
        }
    }
}

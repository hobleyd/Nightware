using System.Net;

using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Core.Http;

using Polly;
using Polly.Timeout;

namespace Nightware.Adapters.Dav.Tests.Core.Http;

public sealed class DavResilienceTests
{
    [Theory]
    [InlineData(500, true)]
    [InlineData(502, true)]
    [InlineData(503, true)]
    [InlineData(504, true)]
    [InlineData(507, false)] // one user's quota
    [InlineData(429, false)]
    [InlineData(404, false)]
    [InlineData(412, false)]
    public void Only_server_wide_failures_count_towards_the_circuit(int status, bool counts) =>
        DavResilience.IsServerFailure(Outcome.FromResult(new HttpResponseMessage((HttpStatusCode)status))).ShouldBe(counts);

    [Fact]
    public void Network_errors_and_attempt_timeouts_count_but_cancellation_does_not()
    {
        DavResilience.IsServerFailure(Outcome.FromException<HttpResponseMessage>(new HttpRequestException())).ShouldBeTrue();
        DavResilience.IsServerFailure(Outcome.FromException<HttpResponseMessage>(new TimeoutRejectedException())).ShouldBeTrue();
        DavResilience.IsServerFailure(Outcome.FromException<HttpResponseMessage>(new OperationCanceledException())).ShouldBeFalse();
    }

    [Fact]
    public void The_circuit_opens_only_on_a_sustained_failure_rate()
    {
        var breaker = DavResilience.CircuitBreaker();

        (breaker.FailureRatio, breaker.MinimumThroughput, breaker.SamplingDuration, breaker.BreakDuration)
            .ShouldBe((0.5, 20, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public async Task An_open_circuit_answers_without_calling_the_server()
    {
        var calls = 0;
        var builder = new ResiliencePipelineBuilder<HttpResponseMessage>();
        DavResilience.Configure(builder, new DavOptions { MaxRetryAttempts = 0 });
        var pipeline = builder.Build();

        for (var i = 0; i < 25; i++)
        {
            try
            {
                using var response = await pipeline.ExecuteAsync(_ =>
                {
                    calls++;
                    return ValueTask.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                }, TestContext.Current.CancellationToken);
            }
            catch (Polly.CircuitBreaker.BrokenCircuitException)
            {
            }
        }

        calls.ShouldBe(20);
    }

    [Fact]
    public void Only_a_streamed_body_is_an_upload()
    {
        using var stream = new HttpRequestMessage(HttpMethod.Put, "https://nc.test/f") { Content = new StreamContent(new MemoryStream()) };
        using var text = new HttpRequestMessage(HttpMethod.Put, "https://nc.test/f") { Content = new StringContent("x") };
        using var none = new HttpRequestMessage(HttpMethod.Get, "https://nc.test/f");

        DavResilience.IsStreamedUpload(stream).ShouldBeTrue();
        DavResilience.IsStreamedUpload(text).ShouldBeFalse();
        DavResilience.IsStreamedUpload(none).ShouldBeFalse();
    }

    [Fact]
    public async Task A_streamed_upload_outlives_the_attempt_timeout_and_other_requests_do_not()
    {
        var builder = new ResiliencePipelineBuilder<HttpResponseMessage>();
        DavResilience.Configure(builder, new DavOptions
        {
            MaxRetryAttempts = 0,
            AttemptTimeout = TimeSpan.FromMilliseconds(50),
            TotalTimeout = TimeSpan.FromMilliseconds(50),
            TransferTimeout = TimeSpan.FromSeconds(30),
        });
        var pipeline = builder.Build();

        async Task<HttpResponseMessage> RunAsync(HttpRequestMessage request)
        {
            var context = ResilienceContextPool.Shared.Get(TestContext.Current.CancellationToken);
            context.SetRequestMessage(request);
            try
            {
                return await pipeline.ExecuteAsync(async c =>
                {
                    await Task.Delay(200, c.CancellationToken);
                    return new HttpResponseMessage(HttpStatusCode.Created);
                }, context);
            }
            finally
            {
                ResilienceContextPool.Shared.Return(context);
            }
        }

        using var upload = new HttpRequestMessage(HttpMethod.Put, "https://nc.test/f") { Content = new StreamContent(new MemoryStream()) };
        using var ordinary = new HttpRequestMessage(HttpMethod.Put, "https://nc.test/f") { Content = new StringContent("x") };

        using var uploaded = await RunAsync(upload);
        uploaded.StatusCode.ShouldBe(HttpStatusCode.Created);
        await Should.ThrowAsync<TimeoutRejectedException>(() => RunAsync(ordinary));
    }
}

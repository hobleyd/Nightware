using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Nightware.Api.Graph;
using Nightware.Api.Observability;
using Nightware.Api.Tests.Support;

using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Nightware.Api.Tests.Observability;

/// <summary>OTLP export wiring, correlation on spans and log scopes, and backend-client metrics.</summary>
public sealed class TelemetryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Export_is_off_by_default()
    {
        await using var host = await TestHost.StartAsync();

        host.App.Services.GetService<TracerProvider>().ShouldBeNull();
        host.App.Services.GetService<MeterProvider>().ShouldBeNull();
    }

    [Fact]
    public async Task An_otlp_endpoint_turns_on_traces_metrics_and_logs()
    {
        await using var host = await TestHost.StartAsync(configuration: new Dictionary<string, string?>
        {
            ["Telemetry:Otlp:Endpoint"] = "http://127.0.0.1:4318",
            ["Telemetry:Otlp:Protocol"] = "HttpProtobuf",
        });

        host.App.Services.GetService<TracerProvider>().ShouldNotBeNull();
        host.App.Services.GetService<MeterProvider>().ShouldNotBeNull();
        host.App.Services.GetServices<ILoggerProvider>().ShouldContain(static p => p.GetType().FullName!.StartsWith("OpenTelemetry", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://collector:21")]
    public void A_malformed_endpoint_is_rejected(string endpoint)
    {
        var options = new TelemetryOptions { Otlp = { Endpoint = endpoint } };

        new TelemetryOptionsValidator().Validate(null, options).Failed.ShouldBeTrue();
    }

    [Fact]
    public void Defaults_are_valid_and_export_nothing()
    {
        var options = new TelemetryOptions();

        options.Otlp.Enabled.ShouldBeFalse();
        options.Otlp.Protocol.ShouldBe(OtlpExportProtocol.Grpc);
        new TelemetryOptionsValidator().Validate(null, options).Succeeded.ShouldBeTrue();
        new TelemetryOptionsValidator().Validate(null, new TelemetryOptions { ServiceName = " " }).Failed.ShouldBeTrue();
    }

    [Fact]
    public async Task Request_ids_are_on_the_server_span()
    {
        var spans = new List<Activity>();
        using var listener = Listen(spans);
        await using var host = await TestHost.StartAsync(static s => s.AddGraphProbes(static g => g.MapGet("/probe", static () => Results.Ok())));
        var client = host.AuthorizedClient();
        client.DefaultRequestHeaders.Add("client-request-id", "client-abc");

        var response = await client.GetAsync("/v1.0/probe", Ct);

        var requestId = response.Headers.GetValues("request-id").Single();
        var span = await StoppedSpanAsync(spans, a => a.Kind == ActivityKind.Server && (string?)a.GetTagItem(RequestCorrelationMiddleware.RequestIdTag) == requestId);

        span.GetTagItem(RequestCorrelationMiddleware.ClientRequestIdTag).ShouldBe("client-abc");
    }

    [Fact]
    public async Task Every_log_record_in_a_request_carries_the_request_ids()
    {
        var logs = new CapturingLoggerProvider();
        await using var host = await TestHost.StartAsync(
            s =>
            {
                s.AddSingleton<ILoggerProvider>(logs);
                s.AddGraphProbes(static g => g.MapGet("/log", static (ILogger<TelemetryTests> logger) =>
                {
#pragma warning disable CA1848, CA2254 // a test probe, not production logging
                    logger.LogWarning("probe log");
#pragma warning restore CA1848, CA2254
                    return Results.Ok();
                }));
            });
        var client = host.AuthorizedClient();
        client.DefaultRequestHeaders.Add("client-request-id", "client-xyz");

        var response = await client.GetAsync("/v1.0/log", Ct);

        var requestId = response.Headers.GetValues("request-id").Single();
        var entry = logs.Entries.Single(static e => e.Text.StartsWith("probe log", StringComparison.Ordinal));
        var scope = entry.Scopes.SelectMany(static s => s).ToDictionary(static p => p.Key, static p => p.Value);
        scope[RequestCorrelationMiddleware.RequestIdTag].ShouldBe(requestId);
        scope[RequestCorrelationMiddleware.ClientRequestIdTag].ShouldBe("client-xyz");
    }

    [Fact]
    public async Task The_request_id_is_the_trace_identifier()
    {
        await using var host = await TestHost.StartAsync(static s => s.AddGraphProbes(static g =>
            g.MapGet("/trace-id", static (HttpContext http) => http.TraceIdentifier)));

        var response = await host.AuthorizedClient().GetAsync("/v1.0/trace-id", Ct);

        (await response.Content.ReadAsStringAsync(Ct)).ShouldBe(response.Headers.GetValues("request-id").Single());
    }

    [Fact]
    public async Task Backend_calls_are_measured_per_named_client()
    {
        using var backend = WireMockServer.Start();
        backend.Given(Request.Create().WithPath("/ok").UsingGet()).RespondWith(Response.Create().WithStatusCode(200));
        backend.Given(Request.Create().WithPath("/down").UsingGet()).RespondWith(Response.Create().WithStatusCode(503));
        var measurements = new List<(double Value, Dictionary<string, object?> Tags)>();
        using var meters = new MeterListener();
        meters.InstrumentPublished = static (instrument, l) =>
        {
            if (instrument is { Name: "http.client.request.duration", Meter.Name: "System.Net.Http" })
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        meters.SetMeasurementEventCallback<double>((_, value, tags, _) =>
        {
            lock (measurements)
            {
                measurements.Add((value, tags.ToArray().ToDictionary(static t => t.Key, static t => t.Value)));
            }
        });
        meters.Start();
        await using var host = await TestHost.StartAsync(s => s.AddHttpClient("jmap-api"));
        var http = host.App.Services.GetRequiredService<IHttpClientFactory>().CreateClient("jmap-api");

        (await http.GetAsync($"{backend.Url}/ok", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await http.GetAsync($"{backend.Url}/down", Ct)).StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);

        lock (measurements)
        {
            var tagged = measurements.Where(static m => (string?)m.Tags.GetValueOrDefault(NightwareMetrics.BackendTagName) == "jmap-api").ToList();
            tagged.ShouldContain(static m => Equals(m.Tags["http.response.status_code"], 200));
            tagged.ShouldContain(static m => Equals(m.Tags["http.response.status_code"], 503) && (string?)m.Tags["error.type"] == "503");
        }
    }

    /// <summary>
    /// The one span <paramref name="match"/> picks, once it has stopped. The client can read the
    /// response before the host stops the request's server activity, so the span may land later.
    /// </summary>
    private static async Task<Activity> StoppedSpanAsync(List<Activity> spans, Func<Activity, bool> match)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (true)
        {
            lock (spans)
            {
                if (spans.SingleOrDefault(match) is { } span)
                {
                    return span;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), timeout.Token);
        }
    }

    private static ActivityListener Listen(List<Activity> spans)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "Microsoft.AspNetCore",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (spans)
                {
                    spans.Add(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}

using System.Diagnostics.Metrics;

namespace Nightware.Api.Observability;

/// <summary>
/// The façade's own instruments (meter <c>Nightware.Api</c>). Request rate, latency and status by
/// route come from ASP.NET Core's <c>http.server.request.duration</c>, backend latency and failures
/// from <c>http.client.request.duration</c> (tagged with <see cref="BackendTagName"/>), throttling
/// from <c>aspnetcore.rate_limiting.requests</c> and retries/circuit state from Polly's meter; this
/// adds only what the framework cannot see.
/// </summary>
public sealed class NightwareMetrics
{
    public const string MeterName = "Nightware.Api";

    /// <summary>Tag on backend HttpClient metrics naming the client (e.g. <c>jmap-api</c>, <c>dav</c>).</summary>
    public const string BackendTagName = "nightware.backend.client";

    private readonly Counter<long> _timeouts;

    public NightwareMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        var meter = meterFactory.Create(MeterName);
        _timeouts = meter.CreateCounter<long>(
            "nightware.request.timeouts",
            unit: "{request}",
            description: "Requests answered 503/504 because they overran the façade's request budget.");
    }

    public void RequestTimedOut(string method, string route) =>
        _timeouts.Add(1, new KeyValuePair<string, object?>("http.request.method", method), new KeyValuePair<string, object?>("http.route", route));
}

using System.Net.Http.Metrics;

using Microsoft.Extensions.Http;

namespace Nightware.Api.Observability;

/// <summary>
/// Tags every outgoing request's <c>http.client.request.duration</c> with the named HttpClient that
/// sent it (<see cref="NightwareMetrics.BackendTagName"/>), so backend latency and failures can be
/// charted per adapter client without the Api knowing adapter internals or backend host names.
/// </summary>
internal sealed class BackendClientMetricsFilter : IHttpMessageHandlerBuilderFilter
{
    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return builder =>
        {
            next(builder);
            var name = string.IsNullOrEmpty(builder.Name) ? "default" : builder.Name;
            builder.AdditionalHandlers.Insert(0, new BackendClientTagHandler(name));
        };
    }

    internal sealed class BackendClientTagHandler(string clientName) : DelegatingHandler
    {
        public string ClientName { get; } = clientName;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpMetricsEnrichmentContext.AddCallback(request, Enrich);
            return base.SendAsync(request, cancellationToken);
        }

        private void Enrich(HttpMetricsEnrichmentContext context) =>
            context.AddCustomTag(NightwareMetrics.BackendTagName, ClientName);
    }
}

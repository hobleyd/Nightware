using System.Collections.Concurrent;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

using Nightware.Adapters.DnsHosts.Administration;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;

using WireMock.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Nightware.Adapters.DnsHosts.Tests.Administration;

/// <summary>
/// <c>AddDnsHostsAdministration</c> pointed at one WireMock server standing in for every host (DigitalOcean at
/// <c>/do/</c>, Gandi at <c>/gandi/</c>, Hetzner at <c>/hetzner/</c>, Route 53 at <c>/r53/</c>, Entra ID at
/// <c>/entra/</c>, Resource Manager at <c>/</c>, Google at <c>/google/token</c> and <c>/gdns/</c>), with every log
/// line captured and the clock fixed.
/// </summary>
internal sealed class DnsHostsTestHost : IDisposable
{
    private readonly ServiceProvider _provider;

    public DnsHostsTestHost()
    {
        Server = WireMockServer.Start();
        var settings = new Dictionary<string, string?>
        {
            ["DnsHosts:DigitalOceanApiBaseUrl"] = $"{Server.Url}/do/",
            ["DnsHosts:GandiApiBaseUrl"] = $"{Server.Url}/gandi/",
            ["DnsHosts:HetznerApiBaseUrl"] = $"{Server.Url}/hetzner/",
            ["DnsHosts:Route53ApiBaseUrl"] = $"{Server.Url}/r53/",
            ["DnsHosts:AzureLoginBaseUrl"] = $"{Server.Url}/entra/",
            ["DnsHosts:AzureManagementBaseUrl"] = $"{Server.Url}/",
            ["DnsHosts:GoogleTokenUrl"] = $"{Server.Url}/google/token",
            ["DnsHosts:GoogleDnsBaseUrl"] = $"{Server.Url}/gdns/",
            ["DnsHosts:MaxRetryAttempts"] = "0",
            ["DnsHosts:RequestTimeout"] = "00:00:05",
            ["DnsHosts:AttemptTimeout"] = "00:00:05",
        };

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs));
        services.AddSingleton<TimeProvider>(Clock);
        services.AddDnsHostsAdministration(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public WireMockServer Server { get; }

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 19, 1, 2, 3, TimeSpan.Zero));

    public LogCapture Logs { get; } = new();

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static DnsProviderToken Token(DnsProvider provider, string value) => DnsProviderToken.Parse(provider, value).Value;

    /// <summary>The same JSON, however its strings are escaped.</summary>
    public static void ShouldBeJson(string actual, string expected) =>
        System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(actual), System.Text.Json.Nodes.JsonNode.Parse(expected)).ShouldBeTrue($"{actual} is not {expected}");

    public static MailDomain Domain(string name) => MailDomain.TryParse(name, out MailDomain? d) ? d : throw new ArgumentException(name);

    public IDnsHosting Hosting(DnsProvider provider) =>
        _provider.CreateScope().ServiceProvider.GetServices<IDnsHosting>().Single(h => h.Provider == provider);

    public IReadOnlyList<IDnsHosting> All() => [.. _provider.CreateScope().ServiceProvider.GetServices<IDnsHosting>()];

    public void Stub(string method, string path, string body, int status = 200, int priority = 10, IDictionary<string, string>? query = null, IDictionary<string, string>? headers = null, string contentType = "application/json")
    {
        IRequestBuilder request = Request.Create().WithPath(path).UsingMethod(method);
        foreach ((string name, string value) in query ?? new Dictionary<string, string>())
        {
            request = request.WithParam(name, value);
        }

        IResponseBuilder response = Response.Create().WithStatusCode(status).WithHeader("Content-Type", contentType).WithBody(body);
        foreach ((string name, string value) in headers ?? new Dictionary<string, string>())
        {
            response = response.WithHeader(name, value);
        }

        Server.Given(request).AtPriority(priority).RespondWith(response);
    }

    public IReadOnlyList<ILogEntry> Requests(string method, string path) =>
        [.. Server.LogEntries.Where(e => e.RequestMessage!.Method == method && e.RequestMessage.Path == path)];

    public IReadOnlyList<ILogEntry> Requests(string method) =>
        [.. Server.LogEntries.Where(e => e.RequestMessage!.Method == method)];

    public void Dispose()
    {
        _provider.Dispose();
        Server.Stop();
        Server.Dispose();
    }

    public sealed class LogCapture : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyCollection<string> Lines => _lines;

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _lines);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                lines.Enqueue($"{category}|{formatter(state, exception)}|{exception}");
        }
    }
}

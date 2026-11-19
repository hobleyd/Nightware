using System.Collections.Concurrent;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Nightware.Adapters.Cloudflare.Administration;
using Nightware.Domain.Administration;

using WireMock.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Nightware.Adapters.Cloudflare.Tests.Administration;

/// <summary><c>AddCloudflareAdministration</c> pointed at a WireMock "Cloudflare" at <c>/client/v4/</c>, with every log line captured.</summary>
internal sealed class CloudflareTestHost : IDisposable
{
    public const string TokenValue = "cf-SECRET-token-0123456789abcdefghijklmn";

    private readonly ServiceProvider _provider;

    public CloudflareTestHost(Action<Dictionary<string, string?>>? configure = null)
    {
        Server = WireMockServer.Start();
        var settings = new Dictionary<string, string?>
        {
            ["Cloudflare:ApiBaseUrl"] = $"{Server.Url}/client/v4/",
            ["Cloudflare:PublicDnsUrl"] = $"{Server.Url}/dns-query",
            ["Cloudflare:MaxRetryAttempts"] = "0",
            ["Cloudflare:RequestTimeout"] = "00:00:05",
            ["Cloudflare:AttemptTimeout"] = "00:00:05",
        };
        configure?.Invoke(settings);

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs));
        services.AddCloudflareAdministration(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public static DnsProviderToken Token { get; } = DnsProviderToken.Parse(DnsProvider.Cloudflare, TokenValue).Value;

    public WireMockServer Server { get; }

    public LogCapture Logs { get; } = new();

    public T Get<T>()
        where T : notnull => _provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    public static string Path(string relative) => "/client/v4/" + relative;

    public void Stub(string method, string relative, string resultJson, int status = 200, int priority = 10, string? param = null, string? paramValue = null)
    {
        IRequestBuilder request = Request.Create().WithPath(Path(relative)).UsingMethod(method);
        if (param is not null)
        {
            request = request.WithParam(param, paramValue!);
        }

        Server.Given(request).AtPriority(priority).RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(resultJson));
    }

    public static string Success(string result, int totalPages = 1) =>
        $$$"""{"success":true,"errors":[],"messages":[],"result":{{{result}}},"result_info":{"page":1,"total_pages":{{{totalPages}}}}}""";

    public static string Failure(int code, string message) =>
        $$"""{"success":false,"errors":[{"code":{{code}},"message":"{{message}}"}],"messages":[],"result":null}""";

    public IReadOnlyList<ILogEntry> Requests(string method, string relative) =>
        [.. Server.LogEntries.Where(e => e.RequestMessage!.Method == method && e.RequestMessage.Path == Path(relative))];

    public static JsonObject Body(ILogEntry entry) => JsonNode.Parse(entry.RequestMessage!.Body!)!.AsObject();

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

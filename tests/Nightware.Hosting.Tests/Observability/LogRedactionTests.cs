using System.Collections.Concurrent;
using System.Net;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Nightware.Hosting.Observability;

namespace Nightware.Hosting.Tests.Observability;

/// <summary>
/// <see cref="LogRedaction.AddLogRedaction"/> on a bare service collection: the pinned categories, the
/// redacting scope provider and HttpClient logging. Each host's <c>LogRedactionTests</c> drives its real
/// pipeline end to end.
/// </summary>
public sealed class LogRedactionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ServiceProvider Build(CapturingLoggerProvider logs, Func<HttpResponseMessage>? respond = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddLogRedaction();
        services.AddHttpClient("backend")
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(respond ?? (static () => new HttpResponseMessage(HttpStatusCode.OK))));
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Request_line_and_matcher_logs_are_pinned_after_configuration()
    {
        var services = new ServiceCollection();
        services.AddLogging(static logging => logging.AddFilter(LogRedaction.HostingDiagnosticsCategory, LogLevel.Trace));
        services.AddLogRedaction();
        using var provider = services.BuildServiceProvider();

        var rules = provider.GetRequiredService<IOptions<LoggerFilterOptions>>().Value.Rules;

        rules.Last(static r => r.CategoryName == LogRedaction.HostingDiagnosticsCategory).LogLevel.ShouldBe(LogLevel.Warning);
        rules.Last(static r => r.CategoryName == LogRedaction.RoutingMatcherCategory).LogLevel.ShouldBe(LogLevel.Information);
        provider.GetRequiredService<ILoggerFactory>().CreateLogger(LogRedaction.HostingDiagnosticsCategory)
            .IsEnabled(LogLevel.Information).ShouldBeFalse();
    }

    [Fact]
    public void The_scope_provider_redacts()
    {
        using var provider = Build(new CapturingLoggerProvider());

        provider.GetRequiredService<IExternalScopeProvider>().ShouldBeOfType<RedactingScopeProvider>();
    }

    [Fact]
    public async Task HttpClient_request_logs_carry_a_redacted_url()
    {
        var logs = new CapturingLoggerProvider();
        using var provider = Build(logs);
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("backend");

        using var response = await client.GetAsync(new Uri("http://nextcloud/public.php/dav/files/SENTINEL-TOKEN/x.txt?secret=SENTINEL-QUERY"), Ct);

        var entries = logs.Entries.Where(static e => e.Category.StartsWith("System.Net.Http.HttpClient.backend.", StringComparison.Ordinal)).ToList();
        entries.Select(static e => e.EventName).ShouldBe(["RequestPipelineStart", "RequestStart", "RequestEnd", "RequestPipelineEnd"], ignoreOrder: true);
        entries.ShouldAllBe(static e => !e.Message.Contains("SENTINEL"));
        entries.First(static e => e.EventName == "RequestStart").Message
            .ShouldBe("Sending HTTP request GET http://nextcloud/public.php/dav/files/{redacted}/x.txt?*");
        entries.First(static e => e.EventName == "RequestPipelineEnd").Message.ShouldEndWith(" - 200");
    }

    [Fact]
    public async Task A_failed_request_is_logged_without_its_url()
    {
        var logs = new CapturingLoggerProvider();
        using var provider = Build(logs, static () => throw new HttpRequestException("refused"));
        var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("backend");

        await Should.ThrowAsync<HttpRequestException>(() => client.GetAsync(new Uri("http://nextcloud/public.php/dav/files/SENTINEL-TOKEN/x"), Ct));

        logs.Entries.ShouldContain(static e => e.EventName == "RequestFailed");
        logs.Entries.ShouldAllBe(static e => !e.Message.Contains("SENTINEL"));
    }

    [Fact]
    public async Task Nothing_is_logged_below_information()
    {
        var logs = new CapturingLoggerProvider();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning).AddProvider(logs));
        services.AddLogRedaction();
        services.AddHttpClient("backend").ConfigurePrimaryHttpMessageHandler(static () => new StubHandler(static () => new HttpResponseMessage(HttpStatusCode.OK)));
        using var provider = services.BuildServiceProvider();

        using var response = await provider.GetRequiredService<IHttpClientFactory>().CreateClient("backend").GetAsync(new Uri("http://backend/x"), Ct);

        logs.Entries.ShouldBeEmpty();
    }

    [Fact]
    public void The_filter_names_an_unnamed_client_default()
    {
        var logs = new CapturingLoggerProvider();
        var filter = new RedactingHttpClientLoggingFilter(new LoggerFactory([logs]));
        var builder = Substitute.For<HttpMessageHandlerBuilder>();
        builder.Name.Returns((string?)null);
        var handlers = new List<DelegatingHandler>();
        builder.AdditionalHandlers.Returns(handlers);

        filter.Configure(static _ => { })(builder);

        handlers.Count.ShouldBe(2);
    }

    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond());
    }

    internal sealed record Entry(string Category, LogLevel Level, string? EventName, string Message);

    internal sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<Entry> _entries = new();

        public IReadOnlyList<Entry> Entries => [.. _entries];

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _entries);

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<Entry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Enqueue(new Entry(category, logLevel, eventId.Name, formatter(state, exception)));
        }
    }
}

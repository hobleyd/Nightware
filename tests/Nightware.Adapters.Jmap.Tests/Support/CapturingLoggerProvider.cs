using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

namespace Nightware.Adapters.Jmap.Tests.Support;

/// <summary>Captures every log line (all categories, Trace and up), including exception text and scopes.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentQueue<string> _lines = new();
    private IExternalScopeProvider? _scopes;

    public IReadOnlyCollection<string> Lines => _lines;

    public string AllText => string.Join('\n', _lines);

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, this);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string category, CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => owner._scopes?.Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            List<string> scopes = [];
            owner._scopes?.ForEachScope(static (scope, list) => list.Add(scope?.ToString() ?? string.Empty), scopes);
            string structured = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join(';', pairs.Select(static p => $"{p.Key}={p.Value}"))
                : string.Empty;
            owner._lines.Enqueue($"{logLevel} {category}: {formatter(state, exception)} | {structured} | {string.Join('/', scopes)} | {exception}");
        }
    }
}

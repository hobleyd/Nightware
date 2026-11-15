using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

using Microsoft.Extensions.Logging;

namespace Nightware.Api.Tests.Support;

/// <summary>
/// Records everything a logger is asked to write — category, formatted message, every structured
/// state value, every scope value and the exception — so tests can assert on what would reach a
/// real sink.
/// </summary>
public sealed class CapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentQueue<CapturedLog> _entries = new();
    private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

    public IReadOnlyList<CapturedLog> Entries => [.. _entries];

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, this);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
    }

    private sealed class Logger(string category, CapturingLoggerProvider owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => owner._scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var text = new StringBuilder();
            text.Append(formatter(state, exception)).Append('\n');
            Append(text, state);

            var scopes = new List<IReadOnlyList<KeyValuePair<string, object?>>>();
            owner._scopes.ForEachScope(
                static (scope, captured) =>
                {
                    var (builder, list) = captured;
                    Append(builder, scope);
                    if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
                    {
                        list.Add([.. pairs]);
                    }
                },
                (text, scopes));

            if (exception is not null)
            {
                text.Append(exception).Append('\n');
            }

            owner._entries.Enqueue(new CapturedLog(category, logLevel, text.ToString(), scopes));
        }

        private static void Append(StringBuilder text, object? value)
        {
            if (value is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                foreach (var (key, item) in pairs)
                {
                    text.Append(key).Append('=').Append(Convert.ToString(item, CultureInfo.InvariantCulture)).Append('\n');
                }
            }

            text.Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append('\n');
        }
    }
}

public sealed record CapturedLog(string Category, LogLevel Level, string Text, IReadOnlyList<IReadOnlyList<KeyValuePair<string, object?>>> Scopes);

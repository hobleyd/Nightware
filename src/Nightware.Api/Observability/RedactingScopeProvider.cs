using System.Collections;
using System.Diagnostics;
using System.Globalization;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Nightware.Api.Observability;

/// <summary>
/// The logger factory's scope provider, with <see cref="UrlRedaction"/> applied to every structured scope. The
/// framework's hosting scope puts <c>RequestPath</c> on every record of a request — console JSON, OTLP log
/// export and any other provider alike, since all of them read scopes from here — and a share id in that path is
/// a secret.
/// </summary>
/// <remarks>
/// Registering an <see cref="IExternalScopeProvider"/> replaces the factory's own, which also adds the trace
/// context (<c>SpanId</c>, <c>TraceId</c>, <c>ParentId</c>, …) chosen by
/// <see cref="LoggerFactoryOptions.ActivityTrackingOptions"/>; that type is internal, so the trace-context scopes
/// are produced here the same way.
/// </remarks>
internal sealed class RedactingScopeProvider(ActivityTrackingOptions tracking) : IExternalScopeProvider
{
    private readonly LoggerExternalScopeProvider _inner = new();

    public IDisposable Push(object? state) => _inner.Push(Redacted(state));

    public void ForEachScope<TState>(Action<object?, TState> callback, TState state)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (tracking != ActivityTrackingOptions.None && Activity.Current is { } activity)
        {
            callback(TraceContext(activity, tracking), state);
            if (tracking.HasFlag(ActivityTrackingOptions.Tags) && activity.TagObjects.Any())
            {
                callback(Redacted(new PairsScope([.. activity.TagObjects], null)), state);
            }

            if (tracking.HasFlag(ActivityTrackingOptions.Baggage) && activity.Baggage.Any())
            {
                callback(Redacted(new PairsScope([.. activity.Baggage.Select(static b => new KeyValuePair<string, object?>(b.Key, b.Value))], null)), state);
            }
        }

        _inner.ForEachScope(callback, state);
    }

    /// <summary>What the factory's own provider adds for <see cref="ActivityTrackingOptions"/>, in its key names.</summary>
    private static PairsScope TraceContext(Activity activity, ActivityTrackingOptions tracking)
    {
        var w3c = activity.IdFormat == ActivityIdFormat.W3C;
        var pairs = new List<KeyValuePair<string, object?>>(5);
        if (tracking.HasFlag(ActivityTrackingOptions.SpanId))
        {
            pairs.Add(new("SpanId", w3c ? activity.SpanId.ToHexString() : activity.Id));
        }

        if (tracking.HasFlag(ActivityTrackingOptions.TraceId))
        {
            pairs.Add(new("TraceId", w3c ? activity.TraceId.ToHexString() : activity.RootId));
        }

        if (tracking.HasFlag(ActivityTrackingOptions.ParentId))
        {
            pairs.Add(new("ParentId", w3c ? activity.ParentSpanId.ToHexString() : activity.ParentId));
        }

        if (tracking.HasFlag(ActivityTrackingOptions.TraceState))
        {
            pairs.Add(new("TraceState", activity.TraceStateString));
        }

        if (tracking.HasFlag(ActivityTrackingOptions.TraceFlags))
        {
            pairs.Add(new("TraceFlags", activity.ActivityTraceFlags));
        }

        return new PairsScope([.. pairs], null);
    }

    /// <summary><paramref name="state"/>, or a copy of it with its secret values redacted.</summary>
    internal static object? Redacted(object? state)
    {
        if (state is not IEnumerable<KeyValuePair<string, object?>> pairs)
        {
            return state is string text ? UrlRedaction.Redact(text) : state;
        }

        KeyValuePair<string, object?>[]? copy = null;
        var index = 0;
        foreach (var pair in pairs)
        {
            var redacted = RedactedValue(pair.Value);
            if (!ReferenceEquals(redacted, pair.Value) && copy is null)
            {
                copy = [.. pairs];
            }

            if (copy is not null)
            {
                copy[index] = new(pair.Key, redacted);
            }

            index++;
        }

        return copy is null ? state : new PairsScope(copy, UrlRedaction.Redact(state.ToString() ?? string.Empty));
    }

    private static object? RedactedValue(object? value) => value switch
    {
        string text when UrlRedaction.Redact(text) is var r && !ReferenceEquals(r, text) => r,
        PathString path when path.Value is { } text && UrlRedaction.Redact(text) is var r && !ReferenceEquals(r, text) => r,
        Uri uri when UrlRedaction.Redact(uri.OriginalString) is var r && !ReferenceEquals(r, uri.OriginalString) => r,
        _ => value,
    };

    /// <summary>A structured scope: its pairs, and what it formats as.</summary>
    private sealed class PairsScope(KeyValuePair<string, object?>[] pairs, string? text) : IReadOnlyList<KeyValuePair<string, object?>>
    {
        public int Count => pairs.Length;

        public KeyValuePair<string, object?> this[int index] => pairs[index];

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => ((IEnumerable<KeyValuePair<string, object?>>)pairs).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => text ?? string.Join(", ", pairs.Select(static p => string.Create(CultureInfo.InvariantCulture, $"{p.Key}:{p.Value}")));
    }
}

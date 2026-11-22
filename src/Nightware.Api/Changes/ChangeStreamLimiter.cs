using System.Collections.Concurrent;

using Microsoft.Extensions.Options;

namespace Nightware.Api.Changes;

/// <summary>
/// Caps the streams one user holds open on this instance (<see cref="ChangeStreamOptions.MaxStreamsPerUser"/>).
/// The route is not a Graph path, so the Graph rate limiter does not see it, and its concurrency limit would be the
/// wrong tool anyway: a stream holds its permit for half an hour.
/// </summary>
public sealed class ChangeStreamLimiter(IOptions<ChangeStreamOptions> options)
{
    private readonly ConcurrentDictionary<string, int> _open = new(StringComparer.Ordinal);

    /// <returns>A lease to dispose when the stream ends, or null when <paramref name="user"/> is at the cap.</returns>
    public IDisposable? TryAcquire(string user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var max = options.Value.MaxStreamsPerUser;
        while (true)
        {
            var current = _open.GetOrAdd(user, 0);
            if (current >= max)
            {
                return null;
            }

            if (_open.TryUpdate(user, current + 1, current))
            {
                return new Lease(this, user);
            }
        }
    }

    /// <summary>Streams <paramref name="user"/> holds open now.</summary>
    public int OpenFor(string user) => _open.TryGetValue(user, out var count) ? count : 0;

    private void Release(string user)
    {
        while (_open.TryGetValue(user, out var current))
        {
            if (current <= 1
                ? _open.TryRemove(new KeyValuePair<string, int>(user, current))
                : _open.TryUpdate(user, current - 1, current))
            {
                return;
            }
        }
    }

    private sealed class Lease(ChangeStreamLimiter owner, string user) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Release(user);
            }
        }
    }
}

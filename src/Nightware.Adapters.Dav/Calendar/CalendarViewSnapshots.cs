using Microsoft.Extensions.Caching.Memory;

using Nightware.Domain.Calendar;

namespace Nightware.Adapters.Dav.Calendar;

/// <summary>
/// A calendarView window's sorted expansion, kept for <see cref="Lifetime"/> so a client paging through it doesn't make
/// Sabre expand and the façade sort the whole window again for every page (the CalDAV expand REPORT can't page).
/// </summary>
/// <remarks>
/// A disposable cache (architecture rule 2): a first page always reads afresh and replaces the snapshot; only a
/// follow-up page (one with a <c>$skiptoken</c>) reads it, so pages of one walk agree with each other and a new walk sees
/// changes at once. Keys name the caller's principal, the calendar and the window, never a token. Bounded by the
/// number of instances held (<see cref="MaxInstances"/>); a window larger than a quarter of that isn't kept.
/// </remarks>
internal sealed class CalendarViewSnapshots : IDisposable
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(1);

    /// <summary>Instances held at once, across every caller; beyond it the cache compacts.</summary>
    public const int MaxInstances = 200_000;

    private readonly MemoryCache _cache;

    public CalendarViewSnapshots(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = MaxInstances, Clock = new Clock(time) });
    }

    public static string KeyFor(string callerPrincipal, string calendarHref, DateTimeOffset start, DateTimeOffset end) =>
        string.Join('\n', callerPrincipal, calendarHref, start.UtcTicks, end.UtcTicks);

    public IReadOnlyList<CalendarEvent>? Get(string key) =>
        _cache.TryGetValue(key, out IReadOnlyList<CalendarEvent>? view) ? view : null;

    public void Put(string key, IReadOnlyList<CalendarEvent> view)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (view.Count > MaxInstances / 4)
        {
            _cache.Remove(key);
            return;
        }

        _cache.Set(key, view, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = Lifetime, Size = Math.Max(1, view.Count) });
    }

    public void Dispose() => _cache.Dispose();

    private sealed class Clock(TimeProvider time) : Microsoft.Extensions.Internal.ISystemClock
    {
        public DateTimeOffset UtcNow => time.GetUtcNow();
    }
}

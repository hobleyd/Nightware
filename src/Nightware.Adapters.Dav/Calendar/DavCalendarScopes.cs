using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Core.Collections;
using Nightware.Adapters.Dav.Core.Discovery;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Calendar;

/// <summary>
/// The event calendars a mailbox's calendar operations address, and the default one (Graph's
/// <c>/me/calendar</c>: where events are created and what <c>calendarView</c> reads).
/// </summary>
/// <param name="Relative">The calendars' hrefs relative to the DAV root, for <see cref="DavItemId.IsWithin"/>.</param>
internal sealed record CalendarScope(DavPrincipal Owner, IReadOnlyList<DavCalendar> Calendars, IReadOnlyList<string> Relative, DavCalendar? Default)
{
    /// <summary>For the caller's own mailbox: other people's calendars shared with the caller (listed, not in <see cref="Contains"/>).</summary>
    public IReadOnlyList<DavCalendar> SharedWithMe { get; init; } = [];

    /// <summary>
    /// True when the id names a resource in one of the mailbox's calendars. An id only proves its href is
    /// inside the DAV root (README "Ids"); this is what stops <c>/me/events/{id}</c> reaching other collections.
    /// </summary>
    public bool Contains(DavItemId id) => Relative.Any(id.IsWithin);
}

/// <summary>
/// Resolves <see cref="CalendarScope"/>s.
/// <list type="bullet">
/// <item>The caller's own mailbox: the caller's own event calendars (not those shared with them).</item>
/// <item>Another mailbox (<c>/users/{x}</c>): that person's calendars shared with the caller, which Nextcloud
/// mounts in the caller's calendar home with <c>owner-principal</c> naming the sharer. The sharer's own home is
/// not readable by anyone else.</item>
/// </list>
/// The default calendar is the writable <c>personal</c> one (Nextcloud's default), else the first writable
/// calendar by order. The read-only birthday calendar is in scope for reads but never the default.
/// </summary>
internal sealed class DavCalendarScopes(IDavPrincipalDiscovery discovery, IDavCollectionCatalog catalog, DavPaths paths, IMemoryCache cache, IOptions<DavOptions> options)
{
    private const string BirthdayCalendar = "contact_birthdays";
    private const string PersonalCalendar = "personal";
    private const string DefaultCachePrefix = "nightware:dav:default-calendar:";

    /// <summary>
    /// The mailbox's default calendar, which is all <c>calendarView</c> reads, without listing the calendar home
    /// each time: remembered for <see cref="DavOptions.DiscoveryCacheDuration"/> per (caller, owner) principal.
    /// Only an href is remembered, never a grant: Nextcloud still checks every request against it. A caller that
    /// finds it gone calls <see cref="ForgetDefault"/> and asks again.
    /// </summary>
    public async Task<Result<DavCalendar?>> DefaultAsync(MailboxRef mailbox, CancellationToken cancellationToken)
    {
        var key = await DefaultKeyAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (key is not null && cache.TryGetValue(key, out DavCalendar? remembered) && remembered is not null)
        {
            return remembered;
        }

        var scope = await ResolveAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!scope.IsSuccess)
        {
            return scope.Error;
        }

        if (key is not null && scope.Value.Default is { } calendar)
        {
            cache.Set(key, calendar, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = options.Value.DiscoveryCacheDuration, Size = 1 });
        }

        return scope.Value.Default;
    }

    /// <summary>Drops the remembered default calendar of <paramref name="mailbox"/> (it was not found).</summary>
    public async Task ForgetDefault(MailboxRef mailbox, CancellationToken cancellationToken)
    {
        if (await DefaultKeyAsync(mailbox, cancellationToken).ConfigureAwait(false) is { } key)
        {
            cache.Remove(key);
        }
    }

    /// <summary>The caller's and the owner's principals (both cached by discovery); null when either is unknown.</summary>
    private async Task<string?> DefaultKeyAsync(MailboxRef mailbox, CancellationToken cancellationToken)
    {
        var owner = await discovery.DiscoverAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!owner.IsSuccess)
        {
            return null;
        }

        if (owner.Value.IsCurrentUser)
        {
            return $"{DefaultCachePrefix}{owner.Value.Href}|{owner.Value.Href}";
        }

        var self = await discovery.CurrentUserAsync(cancellationToken).ConfigureAwait(false);
        return self.IsSuccess ? $"{DefaultCachePrefix}{self.Value.Href}|{owner.Value.Href}" : null;
    }

    public async Task<Result<CalendarScope>> ResolveAsync(MailboxRef mailbox, CancellationToken cancellationToken)
    {
        var owner = await discovery.DiscoverAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!owner.IsSuccess)
        {
            return owner.Error.Kind == ErrorKind.NotFound ? NoCalendar(mailbox) : owner.Error;
        }

        List<DavCalendar> calendars;
        List<DavCalendar> sharedWithMe = [];
        if (owner.Value.IsCurrentUser)
        {
            var own = await catalog.ListCalendarsAsync(owner.Value, cancellationToken).ConfigureAwait(false);
            if (!own.IsSuccess)
            {
                return own.Error;
            }

            calendars = own.Value.Where(c => c.SupportsEvents && !c.IsShared).ToList();
            sharedWithMe = own.Value.Where(c => c.SupportsEvents && c.IsShared).ToList();
        }
        else
        {
            var self = await discovery.CurrentUserAsync(cancellationToken).ConfigureAwait(false);
            if (!self.IsSuccess)
            {
                return self.Error;
            }

            var mine = await catalog.ListCalendarsAsync(self.Value, cancellationToken).ConfigureAwait(false);
            if (!mine.IsSuccess)
            {
                return mine.Error;
            }

            calendars = mine.Value.Where(c => c.SupportsEvents && c.IsShared && IsOwnedBy(c, owner.Value)).ToList();
            if (calendars.Count == 0)
            {
                return NoCalendar(mailbox);
            }
        }

        var relative = calendars.Select(c => paths.ToRelative(c.Href)).OfType<string>().ToList();
        var defaultCalendar = calendars
            .Where(c => !c.IsReadOnly && !IsBirthdays(c))
            .OrderBy(c => SegmentOf(c).StartsWith(PersonalCalendar, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(c => c.Order ?? int.MaxValue)
            .ThenBy(c => c.Href, StringComparer.Ordinal)
            .FirstOrDefault();
        return new CalendarScope(owner.Value, calendars, relative, defaultCalendar) { SharedWithMe = sharedWithMe };
    }

    /// <summary>Nextcloud reports <c>owner-principal</c> relative to the DAV root (<c>principals/users/bob</c>).</summary>
    private static bool IsOwnedBy(DavCalendar calendar, DavPrincipal owner) =>
        calendar.OwnerPrincipal is { Length: > 0 } principal
        && owner.Href.TrimEnd('/').EndsWith("/" + principal.Trim('/'), StringComparison.Ordinal);

    private static bool IsBirthdays(DavCalendar calendar) => string.Equals(SegmentOf(calendar), BirthdayCalendar, StringComparison.Ordinal);

    private static string SegmentOf(DavCalendar calendar) => calendar.Href.TrimEnd('/').Split('/')[^1];

    private static Error NoCalendar(MailboxRef mailbox) =>
        Error.Forbidden($"No calendar of {mailbox.Address} is available to you.");
}

using System.Buffers.Text;
using System.Text.Json;
using Nightware.Adapters.Dav.Core;
using Nightware.Application.Calendar.Calendars;
using Nightware.Application.Common.Query;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Calendar;

/// <summary>
/// <c>calendarView/delta</c> over CalDAV (ADR 0006 D6). The initial sync is the default calendar's expanded view of
/// the window, with the collection's sync token read first, so nothing changed meanwhile is missed. After that,
/// <c>sync-collection</c> names the changed and removed resources: a changed one's occurrences in the window are
/// upserts; a removed one is <c>@removed</c> under its series' id (the occurrences it had are not known without
/// keeping state, so a client drops them by <c>seriesMasterId</c>). An expired token is 410, as for mail.
/// </summary>
internal sealed class DavCalendarViewDelta(DavCalendarScopes scopes, DavCalendarReader reader, IDavClient dav, DavPaths paths) : ICalendarViewDelta
{
    private static readonly ItemQuery Everything = ItemQuery.Default with { Top = int.MaxValue };

    public async Task<Result<DeltaPage<CalendarEvent>>> DeltaAsync(
        MailboxRef mailbox, DateTimeOffset? start, DateTimeOffset? end, DeltaToken? token, CancellationToken cancellationToken)
    {
        var scope = await scopes.ResolveAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!scope.IsSuccess)
        {
            return scope.Error;
        }

        if (scope.Value.Default is not { } calendar)
        {
            return Error.Forbidden($"{mailbox.Address} has no calendar to follow.");
        }

        if (token is { } t)
        {
            var cursor = Cursor.Decode(t);
            return cursor is null
                ? Error.SyncStateGone("The delta token is invalid or has expired; start again from startDateTime and endDateTime.")
                : await ChangesAsync(calendar.Href, mailbox, cursor, cancellationToken).ConfigureAwait(false);
        }

        // The token first (the catalog read it just now): a change after it is replayed next time, even if this
        // view already shows it. Sabre refuses a size-limited sync-collection (507), so the catalog's is used.
        var syncToken = calendar.SyncToken;
        if (syncToken is null)
        {
            var initial = await dav.SyncCollectionAsync(calendar.Href, null, [DavNames.GetETag], null, cancellationToken).ConfigureAwait(false);
            if (!initial.IsSuccess)
            {
                return initial.Error;
            }

            syncToken = initial.Value.SyncToken;
        }

        var view = await reader.ViewAsync(calendar.Href, mailbox, start!.Value, end!.Value, Everything, cancellationToken).ConfigureAwait(false);
        return view.IsSuccess
            ? DeltaPage<CalendarEvent>.Done(Upserts(view.Value.Items), new Cursor(syncToken, start.Value, end.Value).Encode())
            : view.Error;
    }

    private async Task<Result<DeltaPage<CalendarEvent>>> ChangesAsync(string calendarHref, MailboxRef mailbox, Cursor cursor, CancellationToken cancellationToken)
    {
        var sync = await dav.SyncCollectionAsync(calendarHref, cursor.SyncToken, [DavNames.GetETag], null, cancellationToken).ConfigureAwait(false);
        if (!sync.IsSuccess)
        {
            return sync.Error;
        }

        var changed = sync.Value.Changed.Select(r => paths.ToRelative(r.Href)).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var entries = new List<DeltaEntry<CalendarEvent>>();
        if (changed.Count > 0)
        {
            var view = await reader.ViewAsync(calendarHref, mailbox, cursor.Start, cursor.End, Everything, cancellationToken).ConfigureAwait(false);
            if (!view.IsSuccess)
            {
                return view.Error;
            }

            entries.AddRange(Upserts(view.Value.Items.Where(e => ResourceOf(e) is { } href && changed.Contains(href))));
        }

        entries.AddRange(sync.Value.Removed.Select(paths.ToRelative).OfType<string>()
            .Select(static href => (DeltaEntry<CalendarEvent>)new DeltaEntry<CalendarEvent>.Removed(DavItemId.Resource(href).ToEventId().Value, RemovedReason.Deleted)));

        var next = new Cursor(sync.Value.SyncToken, cursor.Start, cursor.End).Encode();
        return sync.Value.IsTruncated ? DeltaPage<CalendarEvent>.More(entries, next) : DeltaPage<CalendarEvent>.Done(entries, next);
    }

    private static List<DeltaEntry<CalendarEvent>> Upserts(IEnumerable<CalendarEvent> events) =>
        [.. events.Select(static e => (DeltaEntry<CalendarEvent>)new DeltaEntry<CalendarEvent>.Upsert(e))];

    private static string? ResourceOf(CalendarEvent e) =>
        DavItemId.Parse((e.SeriesMasterId ?? e.Id).Value, "id") is { IsSuccess: true } id ? id.Value.Href : null;

    /// <summary>The adapter's half of the token: the collection's sync token and the frozen window.</summary>
    private sealed record Cursor(string SyncToken, DateTimeOffset Start, DateTimeOffset End)
    {
        public DeltaToken Encode() =>
            new(Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new[] { SyncToken, Start.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture), End.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture) }, DavCursorJsonContext.Default.StringArray)));

        public static Cursor? Decode(DeltaToken token)
        {
            try
            {
                var parts = JsonSerializer.Deserialize(Base64Url.DecodeFromChars(token.Value), DavCursorJsonContext.Default.StringArray);
                return parts is [var sync, var start, var end]
                    && long.TryParse(start, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var a)
                    && long.TryParse(end, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var b)
                    ? new Cursor(sync, DateTimeOffset.FromUnixTimeSeconds(a), DateTimeOffset.FromUnixTimeSeconds(b))
                    : null;
            }
            catch (Exception ex) when (ex is FormatException or JsonException)
            {
                return null;
            }
        }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string[]))]
internal sealed partial class DavCursorJsonContext : System.Text.Json.Serialization.JsonSerializerContext;

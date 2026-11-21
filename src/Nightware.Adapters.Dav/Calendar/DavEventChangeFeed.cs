using System.Buffers.Text;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

using Nightware.Adapters.Dav.Core;
using Nightware.Application.Subscriptions;
using Nightware.Domain.Kernel;
using Nightware.Domain.Subscriptions;

namespace Nightware.Adapters.Dav.Calendar;

/// <summary>
/// <see cref="IEventChangeFeed"/> over CalDAV <c>sync-collection</c> on the mailbox's default calendar (ADR 0006 D10):
/// no window and no expansion, one change per resource under its series id. A changed event is created when its
/// <c>CREATED</c> is later than the previous poll (less the feed's tolerance), else updated. The state names its
/// calendar, so a default calendar that changes starts over.
/// </summary>
internal sealed partial class DavEventChangeFeed(DavCalendarScopes scopes, IDavClient dav, DavPaths paths) : IEventChangeFeed
{
    public async Task<Result<FeedBaseline>> BaselineAsync(MailboxRef mailbox, CancellationToken cancellationToken)
    {
        var calendar = await DefaultAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!calendar.IsSuccess)
        {
            return calendar.Error;
        }

        // Unlimited: Sabre refuses a size-limited initial sync (507). An initial sync lists every resource.
        var sync = await dav.SyncCollectionAsync(calendar.Value, null, [DavNames.GetETag], null, cancellationToken).ConfigureAwait(false);
        return sync.IsSuccess
            ? new FeedBaseline(Encode(sync.Value.SyncToken, calendar.Value), [.. EventIds(sync.Value.Changed.Where(static r => r.IsFound).Select(static r => r.Href), calendar.Value)])
            : sync.Error;
    }

    /// <summary>The event ids of resources in the calendar (not the calendar itself).</summary>
    private IEnumerable<string> EventIds(IEnumerable<string> hrefs, string calendar)
    {
        var calendarPath = paths.ToRelative(calendar);
        return hrefs.Select(paths.ToRelative).OfType<string>().Where(h => h != calendarPath).Select(static h => DavItemId.Resource(h).ToEventId().Value);
    }

    public async Task<Result<ChangeBatch>> ChangesAsync(MailboxRef mailbox, string state, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var calendar = await DefaultAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!calendar.IsSuccess)
        {
            return calendar.Error;
        }

        if (Decode(state) is not { } cursor || cursor.Calendar != calendar.Value)
        {
            return Error.SyncStateGone("The subscription's calendar state is no longer valid.");
        }

        var sync = await dav.SyncCollectionAsync(calendar.Value, cursor.SyncToken, [DavNames.GetETag], null, cancellationToken).ConfigureAwait(false);
        if (!sync.IsSuccess)
        {
            return sync.Error;
        }

        var calendarPath = paths.ToRelative(calendar.Value);
        var changes = new List<ResourceChange>();
        foreach (var href in sync.Value.Changed.Where(static r => r.IsFound).Select(r => paths.ToRelative(r.Href)).OfType<string>().Where(h => h != calendarPath))
        {
            var resource = await dav.GetAsync(href, cancellationToken).ConfigureAwait(false);
            if (!resource.IsSuccess)
            {
                continue;
            }

            var created = Created(resource.Value.Body);
            changes.Add(new ResourceChange(
                DavItemId.Resource(href).ToEventId().Value,
                created > since - ChangeFeed.CreatedTolerance ? SubscriptionChangeType.Created : SubscriptionChangeType.Updated));
        }

        changes.AddRange(sync.Value.Removed.Select(paths.ToRelative).OfType<string>()
            .Select(static href => new ResourceChange(DavItemId.Resource(href).ToEventId().Value, SubscriptionChangeType.Deleted)));
        return new ChangeBatch(changes, Encode(sync.Value.SyncToken, calendar.Value));
    }

    /// <summary>The master VEVENT's <c>CREATED</c> (UTC), if it has one.</summary>
    internal static DateTimeOffset? Created(string ics)
    {
        var match = CreatedLine().Match(ics);
        return match.Success && DateTimeOffset.TryParseExact(match.Groups[1].Value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var created)
            ? created
            : null;
    }

    private async Task<Result<string>> DefaultAsync(MailboxRef mailbox, CancellationToken cancellationToken)
    {
        var scope = await scopes.ResolveAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!scope.IsSuccess)
        {
            return scope.Error;
        }

        return scope.Value.Default is { } calendar ? calendar.Href : Error.Forbidden($"{mailbox.Address} has no calendar to follow.");
    }

    private static string Encode(string syncToken, string calendar) =>
        Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new[] { syncToken, calendar }, DavCursorJsonContext.Default.StringArray));

    private static (string SyncToken, string Calendar)? Decode(string state)
    {
        try
        {
            return JsonSerializer.Deserialize(Base64Url.DecodeFromChars(state), DavCursorJsonContext.Default.StringArray) is [var sync, var calendar] ? (sync, calendar) : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^CREATED:(\d{8}T\d{6}Z)\r?$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex CreatedLine();
}

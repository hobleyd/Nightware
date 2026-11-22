using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

using Nightware.Adapters.Dav.Core;
using Nightware.Application.Files.Drives;
using Nightware.Domain.Files;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Files.Drive;

/// <summary>
/// <see cref="IDriveDelta"/> from Nextcloud's activity stream (ADR 0008 D5): Files has no <c>sync-collection</c>, but every
/// create, change, rename, move and delete is an activity naming the item's file id.
/// </summary>
/// <remarks>
/// <para>An initial sync lists every item in the home (<c>SEARCH</c>, paged by offset, the root first). Its token holds the
/// newest activity id taken <i>before</i> the listing began, so a change made while the client pages through is reported
/// again afterwards rather than lost.</para>
/// <para>A follow-up reads the activities after that id, looks each named item up by id, and returns it, or a deleted entry
/// when it no longer resolves (deleted, or moved out of the caller's view). The stream answers 304 when nothing happened.</para>
/// </remarks>
internal sealed class NextcloudDriveDelta(DriveHomeLocator homes, DriveItemLocator locator, NextcloudAppRequests app) : IDriveDelta
{
    private const string Activities = "ocs/v2.php/apps/activity/api/v2/activity/files";

    /// <summary>The activity app's own cap on one page.</summary>
    private const int MaxActivities = 500;

    /// <summary>Ids per <c>SEARCH</c> when looking changed items up.</summary>
    private const int IdsPerLookup = 100;

    public async Task<Result<DeltaPage<DriveItem>>> ChangesAsync(DeltaToken? token, int pageSize, bool latest, CancellationToken cancellationToken)
    {
        var home = await homes.CurrentAsync(cancellationToken).ConfigureAwait(false);
        if (!home.IsSuccess)
        {
            return home.Error;
        }

        DriveDeltaToken? state = null;
        if (token is { } given)
        {
            state = DriveDeltaToken.Decode(given.Value);
            if (state is null)
            {
                return Error.SyncStateGone("The delta token is not valid; start a new sync.");
            }
        }

        if (latest || state is null)
        {
            var newest = await NewestActivityAsync(cancellationToken).ConfigureAwait(false);
            if (!newest.IsSuccess)
            {
                return newest.Error;
            }

            if (latest)
            {
                return DeltaPage<DriveItem>.Done([], new DeltaToken(new DriveDeltaToken(newest.Value, null).Encode()));
            }

            state = new DriveDeltaToken(newest.Value, 0);
        }

        return state.Offset is { } offset
            ? await ListingAsync(home.Value, state.ActivityId, offset, pageSize, cancellationToken).ConfigureAwait(false)
            : await FollowUpAsync(home.Value, state.ActivityId, pageSize, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One page of the initial listing: the root on the first page, then every item by name.</summary>
    private async Task<Result<DeltaPage<DriveItem>>> ListingAsync(DriveHome home, long activityId, int offset, int pageSize, CancellationToken cancellationToken)
    {
        var located = new List<LocatedItem>();
        if (offset == 0)
        {
            var root = await locator.PropFindAsync(home, home.Href, cancellationToken).ConfigureAwait(false);
            if (!root.IsSuccess)
            {
                return root.Error;
            }

            located.Add(root.Value);
        }

        var found = await locator.SearchAsync(
            home, DriveItemLocator.NameLike("%"), pageSize + 1, offset, DriveItemLocator.OrderBy(DriveSortKey.Name, descending: false), cancellationToken).ConfigureAwait(false);
        if (!found.IsSuccess)
        {
            return found.Error;
        }

        located.AddRange(found.Value.Take(pageSize));
        var items = await locator.ToItemsAsync(home, located, cancellationToken).ConfigureAwait(false);
        if (!items.IsSuccess)
        {
            return items.Error;
        }

        IReadOnlyList<DeltaEntry<DriveItem>> entries = items.Value.Select(static i => (DeltaEntry<DriveItem>)new DeltaEntry<DriveItem>.Upsert(i)).ToList();
        return found.Value.Count > pageSize
            ? DeltaPage<DriveItem>.More(entries, new DeltaToken(new DriveDeltaToken(activityId, offset + pageSize).Encode()))
            : DeltaPage<DriveItem>.Done(entries, new DeltaToken(new DriveDeltaToken(activityId, null).Encode()));
    }

    /// <summary>The items named by activities after <paramref name="activityId"/>, as they are now.</summary>
    private async Task<Result<DeltaPage<DriveItem>>> FollowUpAsync(DriveHome home, long activityId, int pageSize, CancellationToken cancellationToken)
    {
        var limit = Math.Min(pageSize, MaxActivities);
        var read = await ReadActivitiesAsync($"since={activityId.ToString(CultureInfo.InvariantCulture)}&sort=asc&limit={limit.ToString(CultureInfo.InvariantCulture)}", cancellationToken)
            .ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            return read.Error;
        }

        var (activities, lastGiven) = read.Value;
        if (activities.Count == 0)
        {
            return DeltaPage<DriveItem>.Done([], new DeltaToken(new DriveDeltaToken(activityId, null).Encode()));
        }

        // Each item once, in the order of its last activity, as Graph lists an item once at its latest state.
        var ids = activities.SelectMany(static a => a.ObjectIds).Reverse().Distinct().Reverse().ToList();
        var foundById = new Dictionary<string, LocatedItem>(StringComparer.Ordinal);
        foreach (var batch in ids.Chunk(IdsPerLookup))
        {
            var found = await locator.ByIdsAsync(home, batch, cancellationToken).ConfigureAwait(false);
            if (!found.IsSuccess)
            {
                return found.Error;
            }

            foreach (var item in found.Value)
            {
                if (item.FileId is { } id)
                {
                    foundById[id] = item;
                }
            }
        }

        var present = ids.Select(id => id.ToString(CultureInfo.InvariantCulture)).Where(foundById.ContainsKey).Select(id => foundById[id]).ToList();
        var mapped = await locator.ToItemsAsync(home, present, cancellationToken).ConfigureAwait(false);
        if (!mapped.IsSuccess)
        {
            return mapped.Error;
        }

        var byId = mapped.Value.ToDictionary(static i => i.Id, StringComparer.Ordinal);
        IReadOnlyList<DeltaEntry<DriveItem>> entries = ids
            .Select(id => id.ToString(CultureInfo.InvariantCulture))
            .Select(id => (DeltaEntry<DriveItem>)new DeltaEntry<DriveItem>.Upsert(byId.TryGetValue(id, out var item) ? item : DriveItem.Deleted(id)))
            .ToList();

        var next = new DeltaToken(new DriveDeltaToken(Math.Max(lastGiven ?? 0, activities.Max(static a => a.Id)), null).Encode());
        return activities.Count >= limit ? DeltaPage<DriveItem>.More(entries, next) : DeltaPage<DriveItem>.Done(entries, next);
    }

    /// <summary>The newest activity id now (0 when the stream is empty).</summary>
    private async Task<Result<long>> NewestActivityAsync(CancellationToken cancellationToken)
    {
        var sent = await app.GetAsync($"{Activities}?format=json&sort=desc&limit=1", ocs: true, cancellationToken).ConfigureAwait(false);
        if (!sent.IsSuccess)
        {
            return sent.Error;
        }

        using var response = sent.Value;
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return 0L;
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            return Fail(response.StatusCode);
        }

        if (response.Headers.TryGetValues("X-Activity-First-Known", out var values)
            && long.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var first))
        {
            return first;
        }

        var parsed = Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return parsed.IsSuccess ? parsed.Value.Select(static a => a.Id).DefaultIfEmpty(0).Max() : parsed.Error;
    }

    private async Task<Result<(IReadOnlyList<FileActivity> Activities, long? LastGiven)>> ReadActivitiesAsync(string query, CancellationToken cancellationToken)
    {
        var sent = await app.GetAsync($"{Activities}?format=json&{query}", ocs: true, cancellationToken).ConfigureAwait(false);
        if (!sent.IsSuccess)
        {
            return sent.Error;
        }

        using var response = sent.Value;
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return ((IReadOnlyList<FileActivity>)[], (long?)null);
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            return Fail(response.StatusCode);
        }

        long? lastGiven = response.Headers.TryGetValues("X-Activity-Last-Given", out var values)
            && long.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var last)
                ? last
                : null;
        var parsed = Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        return parsed.IsSuccess ? (parsed.Value, lastGiven) : parsed.Error;
    }

    /// <summary>The <c>ocs.data</c> activities: each one's id and the file ids it names (<c>object_id</c> and the keys of <c>objects</c>).</summary>
    internal static Result<IReadOnlyList<FileActivity>> Parse(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("ocs", out var ocs) || !ocs.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return Error.BackendUnavailable("The groupware server sent an unexpected activity stream.");
            }

            var activities = new List<FileActivity>();
            foreach (var activity in data.EnumerateArray())
            {
                if (!activity.TryGetProperty("activity_id", out var idElement) || !idElement.TryGetInt64(out var id))
                {
                    continue;
                }

                var ids = new List<long>();
                if (activity.TryGetProperty("objects", out var objects) && objects.ValueKind == JsonValueKind.Object)
                {
                    ids.AddRange(objects.EnumerateObject()
                        .Select(static o => long.TryParse(o.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : 0)
                        .Where(static v => v > 0));
                }

                if (activity.TryGetProperty("object_id", out var objectId) && objectId.TryGetInt64(out var single) && single > 0 && !ids.Contains(single))
                {
                    ids.Add(single);
                }

                activities.Add(new FileActivity(id, ids));
            }

            return activities;
        }
        catch (JsonException)
        {
            return Error.BackendUnavailable("The groupware server sent an unreadable activity stream.");
        }
    }

    private static Error Fail(HttpStatusCode status) => status == HttpStatusCode.NotFound
        ? new Error(ErrorKind.Unsupported, DriveErrors.NotSupportedCode, "Delta needs Nextcloud's activity app, which is not enabled.")
        : DavErrors.Map(status, null, null);

    internal sealed record FileActivity(long Id, IReadOnlyList<long> ObjectIds);
}

/// <summary>
/// A drive delta token: the activity id changes are read after, and, during the initial listing, the next offset into it.
/// Unpadded base64url JSON; the Api signs it into the delta link.
/// </summary>
internal sealed record DriveDeltaToken(
    [property: JsonPropertyName("a")] long ActivityId,
    [property: JsonPropertyName("o")] int? Offset)
{
    [JsonPropertyName("v")]
    public int Version { get; init; } = 1;

    public string Encode() => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(this));

    public static DriveDeltaToken? Decode(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 512 || !Base64Url.IsValid(token))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DriveDeltaToken>(Base64Url.DecodeFromChars(token)) is { Version: 1, ActivityId: >= 0 } valid && valid.Offset is null or >= 0
                ? valid
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

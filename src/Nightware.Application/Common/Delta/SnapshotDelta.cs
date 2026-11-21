using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

using Nightware.Domain.Kernel;

namespace Nightware.Application.Common.Delta;

/// <summary>
/// Delta over a small collection whose backend has no sync token (address books, task lists): the token is a
/// snapshot of each item's id and a hash of what a client shows of it, so the next call reports items that
/// are new or changed, and <c>@removed</c> for those gone.
/// </summary>
public static class SnapshotDelta
{
    private const char FieldSeparator = '\u001F';
    private const char EntrySeparator = '\u001E';
    private const string Version = "s1";

    /// <param name="initialAsPage">
    /// Answer the initial sync with a next page (whose follow-up is the same snapshot, so it reports nothing new)
    /// rather than straight away with the final token, as Graph's To Do lists delta does.
    /// </param>
    public static Result<DeltaPage<T>> Diff<T>(IReadOnlyList<T> current, DeltaToken? token, Func<T, string> id, Func<T, string> state, bool initialAsPage = false)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(state);
        Dictionary<string, string>? previous = null;
        if (token is { } t && (previous = Decode(t)) is null)
        {
            return Error.SyncStateGone("The delta token is invalid or has expired; start again without one.");
        }

        var entries = new List<DeltaEntry<T>>();
        foreach (var item in current)
        {
            if (previous is null || !previous.TryGetValue(id(item), out var hash) || hash != Hash(state(item)))
            {
                entries.Add(new DeltaEntry<T>.Upsert(item));
            }
        }

        if (previous is not null)
        {
            var now = current.Select(id).ToHashSet(StringComparer.Ordinal);
            entries.AddRange(previous.Keys.Where(key => !now.Contains(key)).Select(key => new DeltaEntry<T>.Removed(key, RemovedReason.Deleted)));
        }

        var next = new DeltaToken(Base64Url.EncodeToString(Encoding.UTF8.GetBytes(
            Version + EntrySeparator + string.Join(EntrySeparator, current.Select(item => id(item) + FieldSeparator + Hash(state(item)))))));
        return previous is null && initialAsPage ? DeltaPage<T>.More(entries, next) : DeltaPage<T>.Done(entries, next);
    }

    private static string Hash(string state) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(state)), 0, 8);

    private static Dictionary<string, string>? Decode(DeltaToken token)
    {
        try
        {
            var entries = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(token.Value)).Split(EntrySeparator);
            if (entries[0] != Version)
            {
                return null;
            }

            var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in entries.Skip(1).Where(e => e.Length > 0))
            {
                var fields = entry.Split(FieldSeparator);
                if (fields.Length != 2)
                {
                    return null;
                }

                snapshot[fields[0]] = fields[1];
            }

            return snapshot;
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

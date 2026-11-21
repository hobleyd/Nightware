namespace Nightware.Domain.Kernel;

/// <summary>
/// An opaque continuation produced and consumed by the adapter that owns a
/// collection. The Api only ever embeds it in <c>@odata.nextLink</c> /
/// <c>@odata.deltaLink</c> and hands it back unchanged.
/// </summary>
public readonly record struct DeltaToken(string Value)
{
    public override string ToString() => Value;
}

/// <summary>One page of a collection. <paramref name="SkipToken"/> is an adapter-opaque continuation.</summary>
public sealed record Page<T>(IReadOnlyList<T> Items, string? SkipToken = null)
{
    public static Page<T> Empty { get; } = new([]);

    /// <summary>Every item the query matches, across all pages (Graph <c>@odata.count</c>); set when the query asked for it.</summary>
    public int? Total { get; init; }

    public bool HasMore => SkipToken is not null;
}

/// <summary>Why an item left a delta-tracked collection. The client treats the two differently.</summary>
public enum RemovedReason
{
    /// <summary>The item no longer exists.</summary>
    Deleted,
    /// <summary>The item still exists but left this collection (e.g. moved to another folder).</summary>
    Changed,
}

/// <summary>An entry in a delta feed.</summary>
public abstract record DeltaEntry<T>
{
    private DeltaEntry()
    {
    }

    public sealed record Upsert(T Item) : DeltaEntry<T>;

    public sealed record Removed(string Id, RemovedReason Reason) : DeltaEntry<T>;
}

/// <summary>
/// One page of a delta feed: exactly one of <paramref name="NextPage"/> (more to fetch now)
/// or <paramref name="Final"/> (caught up; poll again later with this) is set.
/// </summary>
public sealed record DeltaPage<T>(IReadOnlyList<DeltaEntry<T>> Entries, DeltaToken? NextPage, DeltaToken? Final)
{
    public static DeltaPage<T> More(IReadOnlyList<DeltaEntry<T>> entries, DeltaToken next) => new(entries, next, null);

    public static DeltaPage<T> Done(IReadOnlyList<DeltaEntry<T>> entries, DeltaToken final) => new(entries, null, final);
}

/// <summary>Streamable binary content (raw MIME, attachment bytes, photos, files).</summary>
public sealed record BlobContent(string ContentType, long? Length, Func<CancellationToken, Task<Stream>> Open)
{
    public string? FileName { get; init; }
}

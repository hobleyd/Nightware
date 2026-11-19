using Nightware.Application.Common.Query;
using Nightware.Application.Contacts;
using Nightware.Application.Directory;
using Nightware.Application.Mail;
using Nightware.Domain.Contacts;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.People;

/// <summary>
/// <c>GET {mailbox}/people</c>: the mailbox owner's relevant people (contacts ∪ directory ∪ the directory's group and
/// shared-mailbox addresses ∪ recent correspondents), ranked by <see cref="PeopleRanker"/> and paged by offset.
/// </summary>
/// <remarks>
/// <para>Each page re-reads the sources and re-ranks against the instant frozen in the
/// <c>$skiptoken</c>; the client asks for 999 a page, so a walk is normally one page.</para>
/// <para>Source failures: a source that does not apply to this mailbox (<see cref="ErrorKind.NotFound"/>,
/// <see cref="ErrorKind.Forbidden"/> — e.g. a shared mailbox without an address book) or is not built
/// (<see cref="ErrorKind.Unsupported"/>) contributes nobody. Recent correspondents are an enrichment and
/// contribute nobody on any failure. Any other failure of contacts or the directory, its addresses included (outage,
/// throttling, credentials), fails the request, so the client records a partial sync instead of caching a ranking
/// silently missing a source.</para>
/// </remarks>
public sealed class ListPeople(
    IContactStore contacts,
    IUserDirectory directory,
    IDirectoryAddresses directoryAddresses,
    IRecentCorrespondents correspondents,
    PeopleRanker ranker,
    TimeProvider timeProvider)
{
    /// <summary>Graph's default <c>/people</c> page size.</summary>
    public const int DefaultPageSize = 10;

    /// <summary>Page size used to read each source.</summary>
    public const int SourcePageSize = 1000;

    /// <summary>Most pages read from one source (bounds the work of a request).</summary>
    public const int MaxSourcePages = 10;

    /// <summary>Most correspondents asked of the mail store.</summary>
    public const int MaxCorrespondents = 500;

    public async Task<Result<Page<Person>>> ExecuteAsync(MailboxRef mailbox, ItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (Unsupported(query) is { } unsupported)
        {
            return Error.Validation($"{unsupported} is not supported on people.", unsupported);
        }

        PeopleSkipToken? token = null;
        if (query.SkipToken is { } skipToken)
        {
            if (!PeopleSkipToken.TryParse(skipToken, out var parsed))
            {
                return Error.Validation("The $skiptoken is not valid.", "$skiptoken");
            }

            token = parsed;
        }

        var rankedAt = token?.RankedAt ?? DateTimeOffset.FromUnixTimeSeconds(timeProvider.GetUtcNow().ToUnixTimeSeconds());
        var offset = token?.Offset ?? 0;
        var top = query.Top ?? DefaultPageSize;

        var contactsTask = ReadAllAsync(q => contacts.ListAsync(mailbox, q, cancellationToken), cancellationToken);
        var usersTask = ReadAllAsync(q => directory.ListAsync(q, cancellationToken), cancellationToken);
        var addressesTask = directoryAddresses.ListAsync(cancellationToken);
        var correspondentsTask = correspondents.ListAsync(mailbox, MaxCorrespondents, cancellationToken);
        await Task.WhenAll(contactsTask, usersTask, addressesTask, correspondentsTask).ConfigureAwait(false);

        var contactList = await contactsTask.ConfigureAwait(false);
        if (!contactList.IsSuccess)
        {
            return contactList.Error;
        }

        var userList = await usersTask.ConfigureAwait(false);
        if (!userList.IsSuccess)
        {
            return userList.Error;
        }

        var addresses = await addressesTask.ConfigureAwait(false);
        if (!addresses.IsSuccess && !DoesNotApply(addresses.Error))
        {
            return addresses.Error;
        }

        IReadOnlyList<DirectoryAddress> addressList = addresses.IsSuccess ? addresses.Value : [];

        var recent = await correspondentsTask.ConfigureAwait(false);
        IReadOnlyList<Correspondent> recentList = recent.IsSuccess ? recent.Value : [];

        var ranked = ranker.Rank(contactList.Value, userList.Value, addressList, recentList, rankedAt);
        var page = ranked.Skip(offset).Take(top).ToList();
        var next = offset + top < ranked.Count ? new PeopleSkipToken(offset + top, rankedAt).ToString() : null;
        return new Page<Person>(page, next);
    }

    private static string? Unsupported(ItemQuery query) =>
        query.Filter is not null ? "$filter"
        : query.Search is not null ? "$search"
        : query.OrderBy.Count > 0 ? "$orderby"
        : query.Skip is not null ? "$skip"
        : query.Expand.Count > 0 ? "$expand"
        : null;

    private static bool DoesNotApply(Error error) => error.Kind is ErrorKind.NotFound or ErrorKind.Forbidden or ErrorKind.Unsupported;

    /// <summary>Every item of a paged source (up to <see cref="MaxSourcePages"/>), or empty when it does not apply.</summary>
    private static async Task<Result<IReadOnlyList<T>>> ReadAllAsync<T>(
        Func<ItemQuery, Task<Result<Page<T>>>> list,
        CancellationToken cancellationToken)
    {
        var items = new List<T>();
        string? skipToken = null;
        for (var pages = 0; pages < MaxSourcePages; pages++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await list(new ItemQuery { Top = SourcePageSize, SkipToken = skipToken }).ConfigureAwait(false);
            if (!page.IsSuccess)
            {
                return DoesNotApply(page.Error) ? items : page.Error;
            }

            items.AddRange(page.Value.Items);
            skipToken = page.Value.SkipToken;
            if (skipToken is null)
            {
                break;
            }
        }

        return items;
    }
}

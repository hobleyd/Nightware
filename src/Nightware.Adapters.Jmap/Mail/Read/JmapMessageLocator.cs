using Nightware.Adapters.Jmap.Core;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Core.Requests;
using Nightware.Adapters.Jmap.Core.Session;
using Nightware.Adapters.Jmap.Mail.Ids;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Mail.Read;

/// <inheritdoc />
/// <remarks>
/// <para>An <c>e.</c> id is the JMAP id itself (no round trip; the caller's own <c>Email/get</c> proves it
/// exists).</para>
/// <para>A <c>d.</c> id is found by listing the Drafts mailbox newest first and confirming each Email's
/// Message-ID with <see cref="MailItemId.MatchesDraft"/>. Stalwart v1.1 cannot search for it: an
/// <c>Email/query</c> <c>header: ["Message-ID", …]</c> condition never matches (with or without angle
/// brackets), and header search that does work is indexed asynchronously, so a draft created a moment
/// ago would not be found. A mailbox listing is immediate. Drafts are few and the one wanted is almost
/// always recent, so the first page usually answers; the walk is bounded by <see cref="MaxPages"/>.</para>
/// <para>Should two drafts share the Message-ID (a replacement whose destroy has not landed), the most
/// recently received one is the current draft.</para>
/// </remarks>
internal sealed class JmapMessageLocator(IJmapClient jmap) : IJmapMessageLocator
{
    /// <summary>Pages of <c>maxObjectsInGet</c> Drafts examined before giving up (5,000 drafts with Stalwart's 500).</summary>
    internal const int MaxPages = 10;

    private static readonly IReadOnlyList<Comparator> NewestFirst = [new Comparator("receivedAt", IsAscending: false)];

    public async Task<Result<string>> ResolveAsync(JmapAccount account, MessageId id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        var parsed = MailItemId.Parse(id);
        if (!parsed.IsSuccess)
        {
            return parsed.Error;
        }

        var item = parsed.Value;
        if (!item.IsDraft)
        {
            return item.Value;
        }

        var mailboxes = await JmapMailboxes.LoadAsync(jmap, account, cancellationToken).ConfigureAwait(false);
        if (!mailboxes.IsSuccess)
        {
            return mailboxes.Error;
        }

        if (MailboxRoles.Find(mailboxes.Value.All, WellKnownFolder.Drafts)?.Id is not { } drafts)
        {
            return NotFound();
        }

        // The ids feed Email/get by back-reference, which is not chunked: a page is at most maxObjectsInGet.
        var pageSize = Math.Max(1, account.Session.Limits.MaxObjectsInGet);
        for (var page = 0; page < MaxPages; page++)
        {
            var request = new JmapRequestBuilder();
            var query = request.Add(JmapMethods.EmailQuery, new EmailQueryRequest(account.AccountId)
            {
                Filter = new EmailFilter { InMailbox = drafts },
                Sort = NewestFirst,
                Position = page * pageSize,
                Limit = pageSize,
            });
            var get = request.Add(
                JmapMethods.EmailGet,
                new EmailGetRequest(account.AccountId) { Properties = ["id", "messageId", "receivedAt"] },
                query.Ref("ids", JmapPaths.Ids));
            var response = await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false);

            // The query's own error explains a failed back-reference better than the get's.
            var queried = response.Bind(r => r.Get(query));
            if (!queried.IsSuccess)
            {
                return queried.Error;
            }

            var candidates = response.Bind(r => r.Get(get));
            if (!candidates.IsSuccess)
            {
                return candidates.Error;
            }

            // Pages run newest first, so the first page holding the draft holds its newest copy.
            var match = candidates.Value.List
                .Where(e => e.Id is not null && item.MatchesDraft(e))
                .OrderByDescending(static e => e.ReceivedAt)
                .FirstOrDefault();
            if (match?.Id is { } jmapId)
            {
                return jmapId;
            }

            if (queried.Value.Ids.Count < pageSize)
            {
                return NotFound();
            }
        }

        return Error.NotFound($"The draft was not among the newest {MaxPages * pageSize} drafts.", "id");
    }

    private static Error NotFound() => Error.NotFound("The specified object was not found in the store.", "id");
}

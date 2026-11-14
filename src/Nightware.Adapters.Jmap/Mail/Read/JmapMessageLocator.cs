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
/// An <c>e.</c> id is the JMAP id itself (no round trip; the caller's own <c>Email/get</c> proves it
/// exists). A <c>d.</c> id costs two: the Drafts mailbox, then <c>Email/query</c> by Message-ID with
/// every candidate confirmed by <see cref="MailItemId.MatchesDraft"/>, because JMAP header matching is
/// "contains". Should two drafts share the Message-ID (a replacement whose destroy has not landed),
/// the most recently received one is the current draft.
/// </remarks>
internal sealed class JmapMessageLocator(IJmapClient jmap) : IJmapMessageLocator
{
    private const int MaxCandidates = 10;

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

        var request = new JmapRequestBuilder();
        var query = request.Add(JmapMethods.EmailQuery, new EmailQueryRequest(account.AccountId) { Filter = item.DraftFilter(drafts), Limit = MaxCandidates });
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

        var match = candidates.Value.List
            .Where(e => e.Id is not null && item.MatchesDraft(e))
            .OrderByDescending(static e => e.ReceivedAt)
            .FirstOrDefault();
        return match?.Id is { } jmapId ? jmapId : NotFound();
    }

    private static Error NotFound() => Error.NotFound("The specified object was not found in the store.", "id");
}

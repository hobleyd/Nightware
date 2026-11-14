using Nightware.Adapters.Jmap.Core;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Core.Requests;
using Nightware.Application.Mail;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Mail.Read;

/// <summary>
/// <see cref="IRecentCorrespondents"/>: who the mailbox owner recently wrote to (recipients of Sent
/// Items) and heard from (senders in the Inbox), ranked by how often, then how recently. One JMAP
/// request over the newest <see cref="Window"/> messages of each folder.
/// </summary>
internal sealed class JmapRecentCorrespondents(IJmapClient jmap) : IRecentCorrespondents
{
    internal const int Window = 200;

    private static readonly string[] Properties = ["id", "from", "to", "cc", "bcc", "receivedAt"];

    public async Task<Result<IReadOnlyList<Correspondent>>> ListAsync(MailboxRef mailbox, int max, CancellationToken cancellationToken)
    {
        if (max <= 0)
        {
            return Array.Empty<Correspondent>();
        }

        var account = await jmap.ResolveAccountAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!account.IsSuccess)
        {
            return account.Error;
        }

        var mailboxes = await JmapMailboxes.LoadAsync(jmap, account.Value, cancellationToken).ConfigureAwait(false);
        if (!mailboxes.IsSuccess)
        {
            return mailboxes.Error;
        }

        var sent = MailboxRoles.Find(mailboxes.Value.All, WellKnownFolder.SentItems)?.Id;
        var inbox = MailboxRoles.Find(mailboxes.Value.All, WellKnownFolder.Inbox)?.Id;
        if (sent is null && inbox is null)
        {
            return Array.Empty<Correspondent>();
        }

        var limit = Math.Min(Window, account.Value.Session.Limits.MaxObjectsInGet);
        var request = new JmapRequestBuilder();
        var sentGet = sent is null ? null : Recent(request, account.Value.AccountId, sent, limit);
        var inboxGet = inbox is null ? null : Recent(request, account.Value.AccountId, inbox, limit);
        var response = await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return response.Error;
        }

        var tally = new Tally(mailbox.Address, account.Value.Name);
        if (sentGet is not null)
        {
            var emails = response.Value.Get(sentGet);
            if (!emails.IsSuccess)
            {
                return emails.Error;
            }

            foreach (var email in emails.Value.List)
            {
                tally.Add([.. email.To ?? [], .. email.Cc ?? [], .. email.Bcc ?? []], email.ReceivedAt);
            }
        }

        if (inboxGet is not null)
        {
            var emails = response.Value.Get(inboxGet);
            if (!emails.IsSuccess)
            {
                return emails.Error;
            }

            foreach (var email in emails.Value.List)
            {
                tally.Add(email.From ?? [], email.ReceivedAt);
            }
        }

        return Result<IReadOnlyList<Correspondent>>.Success(tally.Top(max));
    }

    private static JmapCall<GetResponse<Email>> Recent(JmapRequestBuilder request, string accountId, string mailboxId, int limit)
    {
        var query = request.Add(JmapMethods.EmailQuery, new EmailQueryRequest(accountId)
        {
            Filter = new EmailFilter { InMailbox = mailboxId },
            Sort = [new Comparator("receivedAt", IsAscending: false)],
            Limit = limit,
        });
        return request.Add(JmapMethods.EmailGet, new EmailGetRequest(accountId) { Properties = Properties }, query.Ref("ids", JmapPaths.Ids));
    }

    private sealed class Tally(string ownAddress, string accountName)
    {
        private readonly Dictionary<string, (string Address, string? Name, int Count, DateTimeOffset Last)> _people = new(StringComparer.OrdinalIgnoreCase);

        public void Add(IEnumerable<EmailAddressValue> addresses, DateTimeOffset? at)
        {
            var when = at ?? DateTimeOffset.MinValue;
            foreach (var address in addresses.Where(a => !string.IsNullOrWhiteSpace(a.Email) && !IsOwn(a.Email)).DistinctBy(static a => a.Email, StringComparer.OrdinalIgnoreCase))
            {
                var name = string.IsNullOrWhiteSpace(address.Name) ? null : address.Name;
                _people[address.Email] = _people.TryGetValue(address.Email, out var seen)
                    ? (seen.Address, when >= seen.Last ? name ?? seen.Name : seen.Name ?? name, seen.Count + 1, when > seen.Last ? when : seen.Last)
                    : (address.Email, name, 1, when);
            }
        }

        public IReadOnlyList<Correspondent> Top(int max) =>
        [
            .. _people.Values
                .OrderByDescending(static p => p.Count)
                .ThenByDescending(static p => p.Last)
                .ThenBy(static p => p.Address, StringComparer.OrdinalIgnoreCase)
                .Take(max)
                .Select(static p => new Correspondent(new EmailAddress(p.Address, p.Name), p.Count, p.Last)),
        ];

        private bool IsOwn(string address) =>
            string.Equals(address, ownAddress, StringComparison.OrdinalIgnoreCase)
            || string.Equals(address, accountName, StringComparison.OrdinalIgnoreCase);
    }
}

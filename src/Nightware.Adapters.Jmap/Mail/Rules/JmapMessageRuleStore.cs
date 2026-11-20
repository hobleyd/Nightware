using Nightware.Adapters.Jmap.Core;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Core.Requests;
using Nightware.Adapters.Jmap.Core.Session;
using Nightware.Adapters.Jmap.MailboxSettings;
using Nightware.Application.Mail.Rules;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;
using Nightware.Domain.MailboxSettings;

namespace Nightware.Adapters.Jmap.Mail.Rules;

/// <summary>
/// Inbox rules in Nightware's Sieve script (ADR 0006 D5). The first save takes over a <c>VacationResponse</c>
/// that is switched on, so activating the script does not silently stop the out-of-office.
/// </summary>
internal sealed class JmapMessageRuleStore(IJmapClient jmap, NightwareSieve sieve) : IMessageRuleStore
{
    public async Task<Result<IReadOnlyList<MessageRule>>> ListAsync(MailboxRef mailbox, CancellationToken cancellationToken)
    {
        var account = await jmap.ResolveAccountAsync(mailbox, cancellationToken, JmapCapabilities.Sieve).ConfigureAwait(false);
        if (!account.IsSuccess)
        {
            return account.Error;
        }

        var loaded = await sieve.LoadAsync(account.Value, cancellationToken).ConfigureAwait(false);
        return loaded.Map(static l => (IReadOnlyList<MessageRule>)[.. l.State.Rules.OrderBy(static r => r.Sequence)]);
    }

    public async Task<Result> SaveAsync(MailboxRef mailbox, IReadOnlyList<MessageRule> rules, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var account = await jmap.ResolveAccountAsync(mailbox, cancellationToken, JmapCapabilities.Sieve).ConfigureAwait(false);
        if (!account.IsSuccess)
        {
            return account.Error;
        }

        var loaded = await sieve.LoadAsync(account.Value, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded.Error;
        }

        var autoReply = loaded.Value.State.AutoReply;
        if (!loaded.Value.Exists || !loaded.Value.IsActive)
        {
            // Until now Stalwart's VacationResponse has decided what is replied (ours is absent, or was switched
            // off by enabling it elsewhere): carry that over, so taking the script back changes nothing.
            var vacation = await VacationAsync(account.Value, cancellationToken).ConfigureAwait(false);
            if (!vacation.IsSuccess)
            {
                return vacation.Error;
            }

            autoReply = vacation.Value;
        }

        return await sieve.SaveAsync(account.Value, loaded.Value, new FilterState(rules, autoReply), cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<AutoReplySetting>> VacationAsync(JmapAccount account, CancellationToken cancellationToken)
    {
        if (!account.Session.HasAccountCapability(account.AccountId, JmapCapabilities.VacationResponse))
        {
            return new AutoReplySetting(AutoReplyStatus.Disabled);
        }

        var request = new JmapRequestBuilder();
        var get = request.Add(JmapMethods.VacationResponseGet, new GetRequest(account.AccountId) { Ids = [VacationResponse.SingletonId] });
        var response = await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.Bind(r => r.Get(get)).Map(JmapAutoReplyStore.ToDomain);
    }
}

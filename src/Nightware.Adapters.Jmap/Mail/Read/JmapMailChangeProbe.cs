using Nightware.Adapters.Jmap.Core;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Core.Requests;
using Nightware.Application.Changes;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Mail.Read;

/// <summary>
/// The mail area's state for the change stream (ADR 0007): the account's <c>Email</c> and <c>Mailbox</c> states,
/// read in one request by a <c>/get</c> of no ids. The Email state moves with any message change in any folder
/// (arrival, keyword, move, delete); the Mailbox state with folder changes and counts.
/// </summary>
internal sealed class JmapMailChangeProbe(IJmapClient jmap) : IChangeStateProbe
{
    public ChangeArea Area => ChangeArea.Mail;

    public async Task<Result<string>> StateAsync(MailboxRef mailbox, CancellationToken cancellationToken)
    {
        var account = await jmap.ResolveAccountAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!account.IsSuccess)
        {
            return account.Error;
        }

        var request = new JmapRequestBuilder();
        var emails = request.Add(JmapMethods.EmailGet, new EmailGetRequest(account.Value.AccountId) { Ids = [] });
        var mailboxes = request.Add(JmapMethods.MailboxGet, new GetRequest(account.Value.AccountId) { Ids = [] });
        var response = await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return response.Error;
        }

        var emailState = response.Value.Get(emails);
        if (!emailState.IsSuccess)
        {
            return emailState.Error;
        }

        var mailboxState = response.Value.Get(mailboxes);
        return mailboxState.IsSuccess ? $"{emailState.Value.State}|{mailboxState.Value.State}" : mailboxState.Error;
    }
}

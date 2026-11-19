using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration;

/// <summary>Whether an address can be given to a new list or shared mailbox: nobody has it yet (ADR 0003 D7).</summary>
internal static class AddressAvailability
{
    /// <summary>No user, mailbox, shared mailbox, alias or list has the address. The mail server refuses a clash too; this says whose it is first.</summary>
    public static async Task<Result> RequireFreeAsync(IIdentityAccounts identities, IMailingLists lists, IMailAccounts mail, MailAddress address, CancellationToken cancellationToken)
    {
        Result<IdentityAccount> user = await identities.FindByEmailAsync(address.Value, cancellationToken).ConfigureAwait(false);
        if (user.IsSuccess)
        {
            return Error.Conflict($"{address} is {user.Value.DisplayName}'s address.", "address");
        }

        Result<MailingList> list = await lists.FindByAddressAsync(address, cancellationToken).ConfigureAwait(false);
        if (list.IsSuccess)
        {
            return Error.Conflict($"{address} is already a list's address.", "address");
        }

        Result<MailAccount> mailbox = await mail.FindByAddressAsync(address.Value, cancellationToken).ConfigureAwait(false);
        if (mailbox.IsSuccess)
        {
            return Error.Conflict(
                mailbox.Value switch
                {
                    { IsIndividual: true } => $"{address} already belongs to the mailbox {mailbox.Value.Address}.",
                    { IsSharedMailbox: true } => $"{address} already belongs to the shared mailbox {mailbox.Value.Address}.",
                    _ => $"{address} is already an address of the list {mailbox.Value.Address}.",
                },
                "address");
        }

        // A lookup that failed for another reason cannot vouch for the address.
        Error? unanswered = new[] { user.Error, list.Error, mailbox.Error }.FirstOrDefault(static e => e is not null && e.Kind != ErrorKind.NotFound);
        return unanswered is null ? Result.Success() : unanswered;
    }
}

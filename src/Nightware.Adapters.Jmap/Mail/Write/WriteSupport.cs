using Nightware.Adapters.Jmap.Core;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Core.Session;
using Nightware.Adapters.Jmap.Mail.Ids;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Mail.Write;

/// <summary>A message a write targets: its account and current JMAP id.</summary>
internal sealed record LocatedMessage(JmapAccount Account, string JmapId)
{
    public string AccountId => Account.AccountId;
}

internal static class WriteSupport
{
    /// <summary>The account holding <paramref name="mailbox"/> and the JMAP id <paramref name="id"/> denotes now.</summary>
    public static async Task<Result<LocatedMessage>> LocateAsync(
        this IJmapClient jmap, IJmapMessageLocator locator, MailboxRef mailbox, MessageId id, CancellationToken cancellationToken)
    {
        var account = await jmap.ResolveAccountAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!account.IsSuccess)
        {
            return account.Error;
        }

        var jmapId = await locator.ResolveAsync(account.Value, id, cancellationToken).ConfigureAwait(false);
        return jmapId.Map(value => new LocatedMessage(account.Value, value));
    }

    /// <summary>The one Email a <c>Email/get</c> by id returned, or NotFound.</summary>
    public static Result<Email> Single(GetResponse<Email> response) =>
        response.List is [var email, ..] ? email : MessageNotFound();

    /// <summary>Continues a successful <see cref="Result{T}"/> with an operation that has no value.</summary>
    public static Result Then<T>(this Result<T> result, Func<T, Result> next) =>
        result.IsSuccess ? next(result.Value) : result.Error;

    public static Error MessageNotFound() => Error.NotFound("The specified object was not found in the store.");

    public static Error NotADraft() => Error.Validation("Only a draft can be changed or sent this way.", "id");
}

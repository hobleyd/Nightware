using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Mail.Write;

/// <summary>
/// Graph <c>PATCH messages/{id}</c>: <c>isRead</c> on received mail, the content of a draft, or both.
/// The draft keeps its id (ADR 0001 §1). The response is the message re-read with the request's projection.
/// </summary>
public sealed class UpdateMessage(IMailMessageWriter writer, IDraftStore drafts, IMailMessageReader reader)
{
    public async Task<Result<MailMessage>> ExecuteAsync(MailboxRef mailbox, MessageId id, MessageUpdate update, MessageFetch fetch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(fetch);

        if (update.Draft is { } draft)
        {
            var content = await ContentAsync(mailbox, id, draft, cancellationToken).ConfigureAwait(false);
            if (!content.IsSuccess)
            {
                return content.Error;
            }

            var replaced = await drafts.ReplaceAsync(mailbox, id, content.Value, cancellationToken).ConfigureAwait(false);
            if (!replaced.IsSuccess)
            {
                return replaced.Error;
            }

            id = replaced.Value;
        }

        if (update.HasPatch)
        {
            var patched = await writer.PatchAsync(mailbox, id, update.Patch!, cancellationToken).ConfigureAwait(false);
            if (!patched.IsSuccess)
            {
                return patched.Error;
            }
        }

        return await reader.GetAsync(mailbox, id, fetch, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The full new content; reads the draft only for the fields the PATCH left out.</summary>
    private async Task<Result<DraftContent>> ContentAsync(MailboxRef mailbox, MessageId id, DraftUpdate draft, CancellationToken cancellationToken)
    {
        var missing = draft.Missing();
        if (missing.Count == 0)
        {
            return draft.Over(null);
        }

        var current = await reader.GetAsync(mailbox, id, new MessageFetch(missing), cancellationToken).ConfigureAwait(false);
        return current.Map(draft.Over);
    }
}

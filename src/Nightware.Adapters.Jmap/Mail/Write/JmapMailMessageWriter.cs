using System.Text.Json.Nodes;
using Nightware.Adapters.Jmap.Core;
using Nightware.Adapters.Jmap.Core.Errors;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Core.Requests;
using Nightware.Adapters.Jmap.Mail.Ids;
using Nightware.Application.Mail;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Mail.Write;

/// <summary>
/// Keyword and mailbox changes to a message (<c>Email/set update</c>) and its deletion. JMAP ids are stable
/// across moves, so a message keeps its Graph id (ADR 0001 §2).
/// </summary>
internal sealed class JmapMailMessageWriter(IJmapClient jmap, IJmapMessageLocator locator) : IMailMessageWriter
{
    private static readonly IReadOnlyList<string> PlacementProperties = ["id", "keywords", "messageId", "mailboxIds"];

    public async Task<Result> PatchAsync(MailboxRef mailbox, MessageId id, MessagePatch patch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(patch);
        var keywords = KeywordPatch(patch);
        if (!keywords.IsSuccess)
        {
            return keywords.Error;
        }

        var located = await jmap.LocateAsync(locator, mailbox, id, cancellationToken).ConfigureAwait(false);
        if (!located.IsSuccess)
        {
            return located.Error;
        }

        if (keywords.Value.Count == 0)
        {
            return Result.Success();
        }

        return await UpdateAsync(located.Value, keywords.Value, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<MessageId>> MoveAsync(MailboxRef mailbox, MessageId id, FolderRef destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var placed = await PlacementAsync(mailbox, id, cancellationToken).ConfigureAwait(false);
        if (!placed.IsSuccess)
        {
            return placed.Error;
        }

        var (located, email, mailboxes) = placed.Value;
        var target = WriteMailboxes.Resolve(mailboxes, destination);
        if (!target.IsSuccess)
        {
            return target.Error;
        }

        if (target.Value.IsRoot)
        {
            return Error.Validation("A message cannot be moved to the mailbox root.", "destinationId");
        }

        var moved = await UpdateAsync(located, MailboxPatch(target.Value.MailboxId!), cancellationToken).ConfigureAwait(false);
        return moved.IsSuccess
            ? MailItemId.For(located.JmapId, email.Keywords, email.MessageId).ToMessageId()
            : moved.Error!;
    }

    public async Task<Result> SoftDeleteAsync(MailboxRef mailbox, MessageId id, CancellationToken cancellationToken)
    {
        var placed = await PlacementAsync(mailbox, id, cancellationToken).ConfigureAwait(false);
        if (!placed.IsSuccess)
        {
            return placed.Error;
        }

        var (located, email, mailboxes) = placed.Value;
        var trash = WriteMailboxes.IdOfRole(mailboxes, MailboxRoles.Trash);
        if (trash is null)
        {
            return Error.NotFound("The mailbox has no Deleted Items folder.");
        }

        // Graph semantics: a delete moves to Deleted Items; deleting from there is final.
        return email.MailboxIds?.ContainsKey(trash) == true
            ? await DestroyAsync(located, cancellationToken).ConfigureAwait(false)
            : await UpdateAsync(located, MailboxPatch(trash), cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result> PermanentDeleteAsync(MailboxRef mailbox, MessageId id, CancellationToken cancellationToken)
    {
        var located = await jmap.LocateAsync(locator, mailbox, id, cancellationToken).ConfigureAwait(false);
        return located.IsSuccess
            ? await DestroyAsync(located.Value, cancellationToken).ConfigureAwait(false)
            : located.Error;
    }

    /// <summary>
    /// The keyword changes for a patch. NightMail only patches <c>isRead</c>; a flag maps to <c>$flagged</c>.
    /// JMAP has no "complete" flag, and categories and importance live in the immutable message, so those are 501.
    /// </summary>
    internal static Result<PatchObject> KeywordPatch(MessagePatch patch)
    {
        if (patch.Categories is not null || patch.Importance is not null)
        {
            return Error.Unsupported("Only isRead and flag can be changed on a message.");
        }

        var builder = new Patch();
        var count = 0;
        if (patch.IsRead is { } isRead)
        {
            builder.SetEntry("keywords", Keywords.Seen, isRead);
            count++;
        }

        if (patch.Flag is { } flag)
        {
            if (flag.Status == FlagStatus.Complete)
            {
                return Error.Unsupported("A completed flag cannot be stored for a message.");
            }

            builder.SetEntry("keywords", Keywords.Flagged, flag.Status == FlagStatus.Flagged);
            count++;
        }

        return count == 0 ? new PatchObject() : builder.Build();
    }

    /// <summary>Replaces every mailbox of the message: Graph's move leaves it in exactly one folder.</summary>
    private static PatchObject MailboxPatch(string mailboxId) =>
        new Patch().Set("mailboxIds", new JsonObject { [mailboxId] = true }).Build();

    private async Task<Result<(LocatedMessage Located, Email Email, IReadOnlyList<Mailbox> Mailboxes)>> PlacementAsync(
        MailboxRef mailbox, MessageId id, CancellationToken cancellationToken)
    {
        var located = await jmap.LocateAsync(locator, mailbox, id, cancellationToken).ConfigureAwait(false);
        if (!located.IsSuccess)
        {
            return located.Error;
        }

        var request = new JmapRequestBuilder();
        var mailboxes = request.Add(JmapMethods.MailboxGet, WriteMailboxes.GetAll(located.Value.AccountId));
        var emails = request.Add(JmapMethods.EmailGet, new EmailGetRequest(located.Value.AccountId) { Ids = [located.Value.JmapId], Properties = PlacementProperties });
        var response = await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess)
        {
            return response.Error;
        }

        var list = response.Value.Get(mailboxes);
        if (!list.IsSuccess)
        {
            return list.Error;
        }

        return response.Value.Get(emails).Bind(WriteSupport.Single)
            .Map(email => (located.Value, email, list.Value.List));
    }

    private async Task<Result> UpdateAsync(LocatedMessage located, PatchObject patch, CancellationToken cancellationToken)
    {
        var request = new JmapRequestBuilder();
        var set = request.Add(JmapMethods.EmailSet, new SetRequest<Email>(located.AccountId)
        {
            Update = new Dictionary<string, PatchObject>(StringComparer.Ordinal) { [located.JmapId] = patch },
        });
        var response = await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.Bind(r => r.Get(set)).Then(s => s.Updated(located.JmapId));
    }

    private async Task<Result> DestroyAsync(LocatedMessage located, CancellationToken cancellationToken)
    {
        var request = new JmapRequestBuilder();
        var set = request.Add(JmapMethods.EmailSet, new SetRequest<Email>(located.AccountId) { Destroy = [located.JmapId] });
        var response = await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.Bind(r => r.Get(set)).Then(s => s.Destroyed(located.JmapId));
    }
}

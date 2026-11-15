using Nightware.Adapters.Jmap.Core;
using Nightware.Application.Common.Query;
using Nightware.Application.Mail;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Mail.Read;

/// <summary><see cref="IMailFolderReader"/> over JMAP <c>Mailbox/get</c>.</summary>
internal sealed class JmapMailFolderReader(IJmapClient jmap) : IMailFolderReader
{
    public async Task<Result<Page<MailFolder>>> ListAsync(MailboxRef mailbox, FolderRef? parent, ItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var offset = Paging.Offset(query);
        if (!offset.IsSuccess)
        {
            return offset.Error;
        }

        var loaded = await LoadAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded.Error;
        }

        var mailboxes = loaded.Value;
        string? parentId = null;
        if (parent is not null && !MailboxRoles.IsRoot(parent))
        {
            var resolved = mailboxes.Resolve(parent);
            if (!resolved.IsSuccess)
            {
                return resolved.Error;
            }

            parentId = resolved.Value.Id;
        }

        var children = mailboxes.ChildrenOf(parentId);
        var size = Paging.Size(query, int.MaxValue);
        var page = children.Skip(offset.Value).Take(size).Select(mailboxes.ToFolder).ToList();
        return new Page<MailFolder>(page, Paging.Next(offset.Value, page.Count, children.Count));
    }

    public async Task<Result<MailFolder>> GetAsync(MailboxRef mailbox, FolderRef folder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var loaded = await LoadAsync(mailbox, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded.Error;
        }

        var mailboxes = loaded.Value;
        return MailboxRoles.IsRoot(folder) ? mailboxes.Root() : mailboxes.Resolve(folder).Map(mailboxes.ToFolder);
    }

    private async Task<Result<JmapMailboxes>> LoadAsync(MailboxRef mailbox, CancellationToken cancellationToken)
    {
        var account = await jmap.ResolveAccountAsync(mailbox, cancellationToken).ConfigureAwait(false);
        return account.IsSuccess
            ? await JmapMailboxes.LoadAsync(jmap, account.Value, cancellationToken).ConfigureAwait(false)
            : account.Error;
    }
}

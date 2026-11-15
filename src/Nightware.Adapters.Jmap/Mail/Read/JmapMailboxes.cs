using Nightware.Adapters.Jmap.Core;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Core.Requests;
using Nightware.Adapters.Jmap.Core.Session;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Mail.Read;

/// <summary>
/// An account's mailboxes, fetched whole (a mailbox list is small) and resolved from Graph folder
/// references. Folder ids are JMAP mailbox ids used as-is (ADR 0001 §3); the root is synthetic (§40).
/// </summary>
internal sealed class JmapMailboxes
{
    private static readonly string[] Properties = ["id", "name", "parentId", "role", "sortOrder", "totalEmails", "unreadEmails"];

    private JmapMailboxes(IReadOnlyList<Mailbox> all) => All = all;

    public IReadOnlyList<Mailbox> All { get; }

    public static async Task<Result<JmapMailboxes>> LoadAsync(IJmapClient jmap, JmapAccount account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jmap);
        ArgumentNullException.ThrowIfNull(account);
        var request = new JmapRequestBuilder();
        var get = request.Add(JmapMethods.MailboxGet, new GetRequest(account.AccountId) { Properties = Properties });
        var response = await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.Bind(r => r.Get(get)).Map(static r => new JmapMailboxes(r.List));
    }

    internal static JmapMailboxes From(IReadOnlyList<Mailbox> all) => new(all);

    /// <summary>The mailbox <paramref name="folder"/> names; NotFound for unknown ids and for the root, which has none.</summary>
    public Result<Mailbox> Resolve(FolderRef folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var mailbox = folder switch
        {
            _ when MailboxRoles.IsRoot(folder) => null,
            FolderRef.WellKnown w => MailboxRoles.Find(All, w.Name),
            FolderRef.ById b => All.FirstOrDefault(m => string.Equals(m.Id, b.Id.Value, StringComparison.Ordinal)),
            _ => null,
        };
        return mailbox is null ? Error.NotFound("The specified folder could not be found in the store.", "folderId") : mailbox;
    }

    /// <summary>Mailboxes directly under <paramref name="parentId"/> (null = top level), in display order.</summary>
    public IReadOnlyList<Mailbox> ChildrenOf(string? parentId) =>
        [.. All.Where(m => string.Equals(m.ParentId, parentId, StringComparison.Ordinal))
            .OrderBy(static m => m.Role is null ? 1 : 0)
            .ThenBy(static m => m.SortOrder ?? 0)
            .ThenBy(static m => m.Name, StringComparer.OrdinalIgnoreCase)];

    public MailFolder ToFolder(Mailbox mailbox)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        var id = mailbox.Id ?? throw new ArgumentException("A mailbox without an id.", nameof(mailbox));
        return new MailFolder(
            new FolderId(id),
            mailbox.Name ?? string.Empty,
            mailbox.ParentId is null ? MailboxRoles.RootFolderId : new FolderId(mailbox.ParentId),
            All.Count(m => string.Equals(m.ParentId, id, StringComparison.Ordinal)),
            mailbox.UnreadEmails ?? 0,
            mailbox.TotalEmails ?? 0)
        {
            WellKnownName = MailboxRoles.WellKnownOf(mailbox.Role),
        };
    }

    /// <summary>The synthetic <c>msgfolderroot</c>: the parent of every top-level mailbox; it holds no mail itself.</summary>
    public MailFolder Root() =>
        new(MailboxRoles.RootFolderId, "Top of Information Store", null, All.Count(static m => m.ParentId is null), 0, 0)
        {
            WellKnownName = WellKnownFolder.MsgFolderRoot,
        };
}

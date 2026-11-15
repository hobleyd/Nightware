using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Mail.Read;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Mail.Write;

/// <summary>A mailbox a write targets: a real JMAP mailbox, or the synthetic root (ADR 0001 §40).</summary>
internal readonly record struct MailboxTarget(string? MailboxId)
{
    public static MailboxTarget Root { get; } = new(null);

    public bool IsRoot => MailboxId is null;
}

/// <summary>The account's mailbox list, as the write ports need it: resolving folders, roles and subtrees.</summary>
internal static class WriteMailboxes
{
    /// <summary><c>Mailbox/get</c> properties for <see cref="ToFolder"/>.</summary>
    public static IReadOnlyList<string> Properties { get; } = ["id", "name", "parentId", "role", "totalEmails", "unreadEmails"];

    /// <summary>A <c>Mailbox/get</c> of every mailbox (small: a user has tens of folders).</summary>
    public static GetRequest GetAll(string accountId) => new(accountId) { Properties = Properties };

    /// <summary>The mailbox <paramref name="folder"/> names; the root for <c>msgfolderroot</c> by name or id.</summary>
    public static Result<MailboxTarget> Resolve(IReadOnlyList<Mailbox> mailboxes, FolderRef folder)
    {
        ArgumentNullException.ThrowIfNull(mailboxes);
        ArgumentNullException.ThrowIfNull(folder);
        if (MailboxRoles.IsRoot(folder))
        {
            return MailboxTarget.Root;
        }

        var mailbox = folder switch
        {
            FolderRef.WellKnown w => MailboxRoles.Find(mailboxes, w.Name),
            FolderRef.ById b => Find(mailboxes, b.Id),
            _ => null,
        };
        return mailbox?.Id is { } id ? new MailboxTarget(id) : NoSuchFolder();
    }

    public static Mailbox? Find(IReadOnlyList<Mailbox> mailboxes, FolderId id) =>
        mailboxes.FirstOrDefault(m => string.Equals(m.Id, id.Value, StringComparison.Ordinal));

    /// <summary>The id of the mailbox with <paramref name="role"/>, or null.</summary>
    public static string? IdOfRole(IReadOnlyList<Mailbox> mailboxes, string role) =>
        mailboxes.FirstOrDefault(m => string.Equals(m.Role, role, StringComparison.OrdinalIgnoreCase))?.Id;

    /// <summary>Every mailbox below <paramref name="id"/>, deepest first (the order <c>Mailbox/set destroy</c> accepts).</summary>
    public static IReadOnlyList<Mailbox> Descendants(IReadOnlyList<Mailbox> mailboxes, string id)
    {
        var result = new List<Mailbox>();
        Collect(id, depth: 0);
        return result;

        void Collect(string parent, int depth)
        {
            // A parentId cycle would be a server bug; the depth bound keeps it from hanging a request.
            if (depth > mailboxes.Count)
            {
                return;
            }

            foreach (var child in mailboxes.Where(m => string.Equals(m.ParentId, parent, StringComparison.Ordinal) && m.Id is not null))
            {
                Collect(child.Id!, depth + 1);
                result.Add(child);
            }
        }
    }

    /// <summary>The Graph folder for <paramref name="mailbox"/>, mapped once, by Mail/Read.</summary>
    public static MailFolder ToFolder(Mailbox mailbox, IReadOnlyList<Mailbox> all) => JmapMailboxes.From(all).ToFolder(mailbox);

    public static Error NoSuchFolder() => Error.NotFound("The specified folder could not be found in the store.");
}

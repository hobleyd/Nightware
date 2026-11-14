using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Mail;

/// <summary>
/// Graph well-known folder names ↔ JMAP mailbox roles (RFC 8621 §2, IANA "IMAP Mailbox Name
/// Attributes"). <see cref="WellKnownFolder.MsgFolderRoot"/> has no mailbox: JMAP mailboxes
/// with a null <c>parentId</c> are its children.
/// </summary>
internal static class MailboxRoles
{
    public const string Inbox = "inbox";
    public const string Sent = "sent";
    public const string Drafts = "drafts";
    public const string Trash = "trash";
    public const string Junk = "junk";
    public const string Archive = "archive";

    /// <summary>Not an IANA role; Stalwart does not create it. Folders without it resolve to NotFound.</summary>
    public const string Outbox = "outbox";

    private static readonly (WellKnownFolder Folder, string Role)[] Map =
    [
        (WellKnownFolder.Inbox, Inbox),
        (WellKnownFolder.SentItems, Sent),
        (WellKnownFolder.Drafts, Drafts),
        (WellKnownFolder.DeletedItems, Trash),
        (WellKnownFolder.JunkEmail, Junk),
        (WellKnownFolder.Archive, Archive),
        (WellKnownFolder.Outbox, Outbox),
    ];

    /// <summary>The JMAP role for a well-known folder; null for <see cref="WellKnownFolder.MsgFolderRoot"/>.</summary>
    public static string? RoleOf(WellKnownFolder folder)
    {
        foreach (var (f, role) in Map)
        {
            if (f == folder)
            {
                return role;
            }
        }

        return null;
    }

    /// <summary>The well-known folder for a JMAP role (case-insensitive), or null for none/unknown roles.</summary>
    public static WellKnownFolder? WellKnownOf(string? role)
    {
        if (string.IsNullOrEmpty(role))
        {
            return null;
        }

        foreach (var (folder, r) in Map)
        {
            if (string.Equals(r, role, StringComparison.OrdinalIgnoreCase))
            {
                return folder;
            }
        }

        return null;
    }

    public static bool IsRoot(WellKnownFolder folder) => folder == WellKnownFolder.MsgFolderRoot;

    /// <summary>The mailbox carrying the role of <paramref name="folder"/>, or null (also for the root).</summary>
    public static Mailbox? Find(IEnumerable<Mailbox> mailboxes, WellKnownFolder folder)
    {
        ArgumentNullException.ThrowIfNull(mailboxes);
        var role = RoleOf(folder);
        return role is null ? null : mailboxes.FirstOrDefault(m => string.Equals(m.Role, role, StringComparison.OrdinalIgnoreCase));
    }
}

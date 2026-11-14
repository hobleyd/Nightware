using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Mail;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Tests.Mail;

public sealed class MailboxRolesTests
{
    [Theory]
    [InlineData(WellKnownFolder.Inbox, "inbox")]
    [InlineData(WellKnownFolder.SentItems, "sent")]
    [InlineData(WellKnownFolder.Drafts, "drafts")]
    [InlineData(WellKnownFolder.DeletedItems, "trash")]
    [InlineData(WellKnownFolder.JunkEmail, "junk")]
    [InlineData(WellKnownFolder.Archive, "archive")]
    [InlineData(WellKnownFolder.Outbox, "outbox")]
    public void Well_known_folders_map_to_roles_and_back(WellKnownFolder folder, string role)
    {
        MailboxRoles.RoleOf(folder).ShouldBe(role);
        MailboxRoles.WellKnownOf(role).ShouldBe(folder);
        MailboxRoles.WellKnownOf(role.ToUpperInvariant()).ShouldBe(folder);
    }

    [Fact]
    public void The_root_has_no_role()
    {
        MailboxRoles.RoleOf(WellKnownFolder.MsgFolderRoot).ShouldBeNull();
        MailboxRoles.IsRoot(WellKnownFolder.MsgFolderRoot).ShouldBeTrue();
        MailboxRoles.IsRoot(WellKnownFolder.Inbox).ShouldBeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("all")]
    [InlineData("important")]
    public void Unknown_roles_have_no_well_known_folder(string? role)
    {
        MailboxRoles.WellKnownOf(role).ShouldBeNull();
    }

    [Fact]
    public void Every_well_known_folder_is_mapped()
    {
        foreach (var folder in Enum.GetValues<WellKnownFolder>().Where(f => f != WellKnownFolder.MsgFolderRoot))
        {
            MailboxRoles.RoleOf(folder).ShouldNotBeNull();
        }
    }

    [Fact]
    public void Find_locates_the_mailbox_by_role()
    {
        Mailbox[] mailboxes =
        [
            new() { Id = "a", Name = "Inbox", Role = "inbox" },
            new() { Id = "b", Name = "Deleted Items", Role = "trash" },
            new() { Id = "c", Name = "Work" },
        ];

        MailboxRoles.Find(mailboxes, WellKnownFolder.DeletedItems)!.Id.ShouldBe("b");
        MailboxRoles.Find(mailboxes, WellKnownFolder.Archive).ShouldBeNull();
        MailboxRoles.Find(mailboxes, WellKnownFolder.MsgFolderRoot).ShouldBeNull();
    }
}

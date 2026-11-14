using Nightware.Adapters.Jmap.Mail;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Tests.Mail;

public sealed class RootFolderTests
{
    [Fact]
    public void Root_is_recognised_by_well_known_name()
    {
        MailboxRoles.IsRoot(new FolderRef.WellKnown(WellKnownFolder.MsgFolderRoot)).ShouldBeTrue();
        MailboxRoles.IsRoot(new FolderRef.WellKnown(WellKnownFolder.Inbox)).ShouldBeFalse();
    }

    [Theory]
    [InlineData("msgfolderroot", true)]
    [InlineData("MsgFolderRoot", true)]
    [InlineData("a1", false)]
    public void Root_is_recognised_by_synthetic_id(string id, bool isRoot) =>
        MailboxRoles.IsRoot(new FolderRef.ById(new FolderId(id))).ShouldBe(isRoot);

    [Fact]
    public void Synthetic_root_id_is_stable() => MailboxRoles.RootFolderId.Value.ShouldBe("msgfolderroot");
}

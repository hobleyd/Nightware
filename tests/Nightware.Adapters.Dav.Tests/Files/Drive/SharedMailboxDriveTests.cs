using Nightware.Adapters.Dav.Core.Discovery;
using Nightware.Adapters.Dav.Files.Drive;
using Nightware.Application.Files.Drives;
using Nightware.Domain.Files;
using Nightware.Domain.Kernel;

using static Nightware.Adapters.Dav.Tests.Files.Drive.DriveAdapterHarness;

namespace Nightware.Adapters.Dav.Tests.Files.Drive;

/// <summary>A shared mailbox's drive: the folder its Nextcloud user shared with the members, mounted in their home (phase 27).</summary>
public sealed class SharedMailboxDriveTests : IDisposable
{
    private const string Desk = "desk@example.com";
    private const string DeskUid = "mbx-desk@example.com";
    private const string Mount = Home + "Helpdesk/";
    private const string SharedWithMe = Ocs + "files_sharing/api/v1/shares";
    private readonly DriveAdapterHarness _nc = new();

    public SharedMailboxDriveTests()
    {
        _nc.Discovery.DiscoverAsync(new MailboxRef(Desk), Arg.Any<CancellationToken>())
            .Returns(new DavPrincipal(DavRoot + "principals/users/mbx-desk%40example.com/", false));
        _nc.Discovery.DiscoverAsync(new MailboxRef("bob@example.com"), Arg.Any<CancellationToken>())
            .Returns(new DavPrincipal(DavRoot + "principals/users/bob/", false));
        _nc.Serve("GET", SharedWithMe, 200,
            """{"ocs":{"meta":{"statuscode":200},"data":[{"id":"9","share_type":0,"uid_owner":"bob","item_type":"folder","file_target":"/From Bob"},{"id":"63","share_type":1,"uid_owner":"mbx-desk@example.com","item_type":"folder","file_target":"/Helpdesk","file_source":983}]}}""",
            contentType: "application/json");
        _nc.Serve("PROPFIND", Mount, 207, Recorded("folder.propfind.xml").Replace("alice/nwfix/", "alice/Helpdesk/", StringComparison.Ordinal), "1");
        _nc.Serve("PROPFIND", Mount, 207, Recorded("folder-self.propfind.xml").Replace("alice/nwfix/", "alice/Helpdesk/", StringComparison.Ordinal), "0");
    }

    public void Dispose() => _nc.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDriveStore Store => _nc.Get<IDriveStore>();

    [Fact]
    public async Task A_members_view_of_a_shared_mailbox_drive_is_its_shared_folder()
    {
        var drive = (await Store.GetDriveOfAsync(Desk, null, Ct)).Value;

        drive.IsShared.ShouldBeTrue();
        drive.Id.ShouldBe(DriveHome.IdOf(DeskUid));
        drive.OwnerId.ShouldBe(DeskUid);
        _nc.Requests("PROPFIND", Mount).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Its_items_are_listed_from_the_mount_and_name_its_drive()
    {
        var page = (await Store.ListChildrenAsync(DriveItemRef.Root with { Owner = Desk }, new DriveListQuery(10, null), Ct)).Value;

        page.Items.ShouldNotBeEmpty();
        page.Items.ShouldAllBe(i => i.DriveId == DriveHome.IdOf(DeskUid));
        page.Items.ShouldAllBe(i => i.ParentPath == "/");
    }

    [Fact]
    public async Task Its_drive_id_reaches_the_same_folder()
    {
        (await Store.GetDriveAsync(DriveHome.IdOf(DeskUid), Ct)).Value.IsShared.ShouldBeTrue();
    }

    [Fact]
    public async Task A_colleagues_drive_is_refused_even_when_they_shared_a_folder()
    {
        (await Store.GetDriveOfAsync("bob@example.com", null, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        (await Store.GetDriveAsync(DriveHome.IdOf("bob"), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task A_shared_mailbox_that_shared_nothing_with_the_caller_is_refused()
    {
        _nc.Discovery.DiscoverAsync(new MailboxRef("other@example.com"), Arg.Any<CancellationToken>())
            .Returns(new DavPrincipal(DavRoot + "principals/users/mbx-other%40example.com/", false));

        (await Store.GetDriveOfAsync("other@example.com", null, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public void A_shared_drives_hrefs_are_the_mount_and_the_callers_uploads()
    {
        var home = new DriveHome("alice") { Mount = "files/alice/Help%20desk/", OwnerUid = DeskUid };

        home.Href.ShouldBe("files/alice/Help%20desk/");
        home.UploadsHref.ShouldBe("uploads/alice/");
        home.DriveId.ShouldBe(DriveHome.IdOf(DeskUid));
        DriveHome.UidOf(home.DriveId).ShouldBe(DeskUid);
        DriveHome.UidOf("not-a-drive").ShouldBeNull();
    }

    [Theory]
    [InlineData("alice", null, "/files/alice")]
    [InlineData("mbx-desk%40example.com", null, "/files/mbx-desk@example.com")]
    [InlineData("alice", "files/alice/Help%20desk/", "/files/alice/Help desk")]
    public void A_search_is_scoped_by_the_decoded_path(string uid, string? mount, string scope) =>
        DriveItemLocator.ScopeOf(new DriveHome(uid) { Mount = mount }).ShouldBe(scope);
}

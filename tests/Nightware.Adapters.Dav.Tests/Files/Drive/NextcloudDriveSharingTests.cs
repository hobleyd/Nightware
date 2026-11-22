using System.Text.Json;

using Nightware.Adapters.Dav.Files.Drive;
using Nightware.Application.Files.Drives;
using Nightware.Domain.Files;
using Nightware.Domain.Kernel;

using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

using static Nightware.Adapters.Dav.Tests.Files.Drive.DriveAdapterHarness;

namespace Nightware.Adapters.Dav.Tests.Files.Drive;

public sealed class NextcloudDriveSharingTests : IDisposable
{
    private const string Shares = Ocs + "files_sharing/api/v1/shares";
    private static readonly DriveItemRef File = DriveItemRef.ById("1748");
    private readonly DriveAdapterHarness _nc = new();

    public NextcloudDriveSharingTests() => _nc.ServeAliceHome();

    public void Dispose() => _nc.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDriveSharing Sharing => _nc.Get<IDriveSharing>();

    private void Json(string method, string path, string body, string? param = null, string? value = null)
    {
        var request = Request.Create().WithPath(path).UsingMethod(method);
        if (param is not null)
        {
            request = request.WithParam(param, value!);
        }

        _nc.Server.Given(request).RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(body));
    }

    [Fact]
    public void Is_registered_by_the_dav_adapter() => Sharing.ShouldBeOfType<NextcloudDriveSharing>();

    [Fact]
    public async Task A_view_link_is_a_read_only_public_share_of_the_items_path()
    {
        Json("POST", Shares, Recorded("share-link.json"));

        var share = (await Sharing.CreateLinkAsync(File, new SharingLinkRequest("view", new DateTimeOffset(2026, 12, 24, 0, 0, 0, TimeSpan.Zero), "pw"), Ct)).Value;

        share.Roles.ShouldBe(["read"]);
        share.Link!.Type.ShouldBe("view");
        share.Link.WebUrl.ShouldContain("/s/");
        var form = _nc.Requests("POST", Shares).Single().RequestMessage!.Body!;
        form.ShouldContain("path=%2Fnwfix%2Fa.txt");
        form.ShouldContain("shareType=3");
        form.ShouldContain("permissions=1");
        form.ShouldContain("expireDate=2026-12-24");
        form.ShouldContain("password=pw");
    }

    [Fact]
    public async Task An_edit_link_on_a_file_may_update_it()
    {
        Json("POST", Shares, Recorded("share-link.json"));

        await Sharing.CreateLinkAsync(File, new SharingLinkRequest("edit", null, null), Ct);

        _nc.Requests("POST", Shares).Single().RequestMessage!.Body!.ShouldContain("permissions=3");
    }

    [Fact]
    public async Task An_invitation_finds_the_user_by_address_and_shares_with_them()
    {
        Json("GET", Ocs + "files_sharing/api/v1/sharees", Recorded("sharees.json"));
        Json("POST", Shares, Recorded("share-user.json"));

        var shares = (await Sharing.InviteAsync(File, new DriveInvitation(["bob@example.com"], ["write"], null), Ct)).Value;

        var share = shares.ShouldHaveSingleItem();
        share.Roles.ShouldBe(["write"]);
        share.GrantedTo.ShouldBe(new EmailAddress("bob@example.com", "Bob Brown"));
        var form = _nc.Requests("POST", Shares).Single().RequestMessage!.Body!;
        form.ShouldContain("shareWith=bob");
        form.ShouldContain("shareType=0");
    }

    [Fact]
    public async Task An_address_that_is_no_user_is_refused()
    {
        Json("GET", Ocs + "files_sharing/api/v1/sharees", """{"ocs":{"meta":{"statuscode":200},"data":{"exact":{"users":[]},"users":[]}}}""");

        (await Sharing.InviteAsync(File, new DriveInvitation(["nobody@example.com"], ["read"], null), Ct)).Error!.Target.ShouldBe("recipients");
        _nc.Requests("POST").ShouldBeEmpty();
    }

    [Fact]
    public async Task Permissions_are_the_items_shares_and_those_inherited_from_above()
    {
        Json("GET", Shares, Recorded("shares.json"));
        Json("GET", Shares + "/inherited", """{"ocs":{"meta":{"statuscode":200},"data":[{"id":"9","share_type":0,"permissions":1,"share_with":"carol","item_source":1746}]}}""");

        var permissions = (await Sharing.ListPermissionsAsync(File, Ct)).Value;

        permissions.Select(p => p.Id).ShouldBe(["42", "41", "9"]);
        permissions[0].GrantedTo!.Address.ShouldBe("bob");
        permissions[1].Link!.Type.ShouldBe("view");
        permissions[2].InheritedFromId.ShouldBe("1746");
    }

    [Fact]
    public async Task An_items_own_permission_is_removed_an_inherited_one_is_not()
    {
        Json("GET", Shares, Recorded("shares.json"));
        Json("GET", Shares + "/inherited", """{"ocs":{"meta":{"statuscode":200},"data":[{"id":"9","share_type":0,"permissions":1,"share_with":"carol","item_source":1746}]}}""");
        Json("DELETE", Shares + "/41", """{"ocs":{"meta":{"statuscode":200},"data":[]}}""");

        (await Sharing.DeletePermissionAsync(File, "41", Ct)).IsSuccess.ShouldBeTrue();
        (await Sharing.DeletePermissionAsync(File, "9", Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        (await Sharing.DeletePermissionAsync(File, "77", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _nc.Requests("DELETE").ShouldHaveSingleItem();
    }

    [Fact]
    public void A_shares_permission_bits_decide_its_role()
    {
        using var read = JsonDocument.Parse("""{"id":"1","share_type":3,"permissions":17,"url":"https://c/s/x","password":"hash"}""");
        using var write = JsonDocument.Parse("""{"id":"2","share_type":0,"permissions":15,"share_with":"bob","expiration":"2026-12-24 00:00:00"}""");

        var link = NextcloudDriveSharing.ToShare(read.RootElement, null);
        var user = NextcloudDriveSharing.ToShare(write.RootElement, null);

        link.Roles.ShouldBe(["read"]);
        link.HasPassword.ShouldBeTrue();
        user.Roles.ShouldBe(["write"]);
        user.ExpirationDateTime.ShouldBe(new DateTimeOffset(2026, 12, 24, 0, 0, 0, TimeSpan.Zero));
    }
}

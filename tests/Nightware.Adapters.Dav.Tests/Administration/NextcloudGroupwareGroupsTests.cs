using System.Text.Json.Nodes;

using Nightware.Adapters.Dav.Administration;
using Nightware.Application.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Tests.Administration;

/// <summary>
/// <see cref="IGroupwareGroups"/> against a WireMock Nextcloud; shapes recorded from Nextcloud with groupfolders
/// 23.0.1 (phase 9 step 0 d, e).
/// </summary>
public sealed class NextcloudGroupwareGroupsTests : IDisposable
{
    private const string Details = "/ocs/v1.php/cloud/groups/details";
    private const string Members = "/ocs/v1.php/cloud/groups/acme-finance/users";
    private const string Group = "/ocs/v1.php/cloud/groups/acme-finance";
    private const string Groups = "/ocs/v1.php/cloud/groups";
    private const string Carol = "/ocs/v1.php/cloud/users/carol/groups";
    private const string Folders = "/index.php/apps/groupfolders/folders";

    private readonly NextcloudAdminServer _nextcloud = new();

    public void Dispose() => _nextcloud.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IGroupwareGroups Groupware => _nextcloud.Get<IGroupwareGroups>();

    private IReadOnlyList<WireMock.RequestMessage> Sent(string method, string path) =>
        [.. _nextcloud.Requests.Select(static e => (WireMock.RequestMessage)e.RequestMessage!).Where(r => r.Method == method && r.AbsolutePath == path)];

    private static JsonObject Body(WireMock.RequestMessage request) => JsonNode.Parse(request.Body!)!.AsObject();

    [Fact]
    public void Is_registered_as_the_port() => Groupware.ShouldBeOfType<NextcloudGroupwareGroups>();

    [Fact]
    public async Task Finds_a_group_by_exact_id_with_its_members()
    {
        _nextcloud.Answer("GET", Details, 200, 100, """{"groups":[{"id":"acme-finance-old","displayname":"Old"},{"id":"acme-finance","displayname":"Finance","usercount":2}]}""");
        _nextcloud.Answer("GET", Members, 200, 100, """{"users":["alice","carol"]}""");

        GroupwareGroup group = (await Groupware.FindAsync("acme-finance", Ct)).Value;

        group.DisplayName.ShouldBe("Finance");
        group.MemberUids.ShouldBe(["alice", "carol"]);
        Sent("GET", Details).Single().Query!["search"].ShouldBe(["acme-finance"]);
    }

    [Fact]
    public async Task A_group_the_search_does_not_return_is_not_found()
    {
        _nextcloud.Answer("GET", Details, 200, 100, """{"groups":[{"id":"acme-finance-old"}]}""");

        (await Groupware.FindAsync("acme-finance", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        Sent("GET", Members).ShouldBeEmpty();
    }

    [Fact]
    public async Task Creates_a_group_with_its_display_name_and_reports_a_taken_id()
    {
        _nextcloud.Answer("POST", Groups, 200, 100);
        (await Groupware.CreateAsync("acme-finance", "Finance", Ct)).IsSuccess.ShouldBeTrue();
        JsonObject body = Body(Sent("POST", Groups).Single());
        body["groupid"]!.GetValue<string>().ShouldBe("acme-finance");
        body["displayname"]!.GetValue<string>().ShouldBe("Finance");

        _nextcloud.Answer("POST", Groups, 200, 102, message: "group exists");
        (await Groupware.CreateAsync("acme-finance", "Finance", Ct)).Error!.Kind.ShouldBe(ErrorKind.Conflict);
    }

    [Fact]
    public async Task Renames_a_group_by_its_display_name()
    {
        _nextcloud.Answer("PUT", Group, 200, 100);

        (await Groupware.SetDisplayNameAsync("acme-finance", "Money", Ct)).IsSuccess.ShouldBeTrue();

        JsonObject body = Body(Sent("PUT", Group).Single());
        body["key"]!.GetValue<string>().ShouldBe("displayname");
        body["value"]!.GetValue<string>().ShouldBe("Money");
    }

    [Theory]
    [InlineData(100, null)]
    [InlineData(102, ErrorKind.NotFound)]
    [InlineData(103, ErrorKind.NotFound)]
    public async Task Adds_a_member_by_query_and_maps_missing_group_or_user(int ocs, ErrorKind? kind)
    {
        _nextcloud.Answer("POST", Carol, 200, ocs);

        Result added = await Groupware.AddMemberAsync("acme-finance", "carol", Ct);

        if (kind is null)
        {
            added.IsSuccess.ShouldBeTrue();
        }
        else
        {
            added.Error!.Kind.ShouldBe(kind.Value);
        }

        Sent("POST", Carol).Single().Query!["groupid"].ShouldBe(["acme-finance"]);
    }

    [Fact]
    public async Task Removes_a_member()
    {
        _nextcloud.Answer("DELETE", Carol, 200, 100);
        (await Groupware.RemoveMemberAsync("acme-finance", "carol", Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Deleting_a_group_that_is_gone_is_not_found()
    {
        _nextcloud.Answer("DELETE", Group, 200, 101);
        (await Groupware.DeleteAsync("acme-finance", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Reads_a_team_folder_shared_with_groups_or_with_no_one()
    {
        _nextcloud.Answer("GET", $"{Folders}/1", 200, 100, """{"id":1,"mount_point":"Finance","groups":{"acme-finance":15},"acl":false,"quota":-3}""");
        _nextcloud.Answer("GET", $"{Folders}/2", 200, 100, """{"id":2,"mount_point":"Old","groups":[],"acl":false}""");

        TeamFolder shared = (await Groupware.FindFolderAsync(1, Ct)).Value;
        TeamFolder alone = (await Groupware.FindFolderAsync(2, Ct)).Value;

        shared.MountPoint.ShouldBe("Finance");
        shared.Groups.ShouldBe(new HashSet<string> { "acme-finance" });
        alone.Groups.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_missing_team_folder_is_not_found()
    {
        _nextcloud.AnswerRaw("GET", $"{Folders}/9", 404, "{}");
        _nextcloud.Answer("GET", $"{Folders}/8", 200, 100, "null");

        (await Groupware.FindFolderAsync(9, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await Groupware.FindFolderAsync(8, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Creates_a_team_folder_by_mount_point()
    {
        _nextcloud.Answer("POST", Folders, 200, 100, """{"id":1,"mount_point":"Finance","quota":-3,"acl":false,"size":0,"groups":[],"manage":[]}""");

        TeamFolder folder = (await Groupware.CreateFolderAsync("Finance", Ct)).Value;

        folder.Id.ShouldBe(1);
        Body(Sent("POST", Folders).Single())["mountpoint"]!.GetValue<string>().ShouldBe("Finance");
    }

    [Fact]
    public async Task Without_the_app_a_team_folder_is_unsupported()
    {
        _nextcloud.AnswerRaw("POST", Folders, 404, "<html>");
        (await Groupware.CreateFolderAsync("Finance", Ct)).Error!.Kind.ShouldBe(ErrorKind.Unsupported);
    }

    [Fact]
    public async Task Sharing_a_folder_adds_the_group_then_sets_its_permissions()
    {
        _nextcloud.Answer("POST", $"{Folders}/1/groups", 200, 100);
        _nextcloud.Answer("POST", $"{Folders}/1/groups/acme-finance", 200, 100);

        (await Groupware.GrantFolderAsync(1, "acme-finance", Ct)).IsSuccess.ShouldBeTrue();

        Body(Sent("POST", $"{Folders}/1/groups").Single())["group"]!.GetValue<string>().ShouldBe("acme-finance");
        Body(Sent("POST", $"{Folders}/1/groups/acme-finance").Single())["permissions"]!.GetValue<int>().ShouldBe(15);
    }

    [Fact]
    public async Task Deletes_a_team_folder_and_reports_one_already_gone()
    {
        _nextcloud.Answer("DELETE", $"{Folders}/1", 200, 100);
        _nextcloud.AnswerRaw("DELETE", $"{Folders}/2", 404, "{}");

        (await Groupware.DeleteFolderAsync(1, Ct)).IsSuccess.ShouldBeTrue();
        (await Groupware.DeleteFolderAsync(2, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task An_id_with_a_slash_is_refused_before_asking()
    {
        (await Groupware.AddMemberAsync("acme/finance", "carol", Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        _nextcloud.Requests.ShouldBeEmpty();
    }
}

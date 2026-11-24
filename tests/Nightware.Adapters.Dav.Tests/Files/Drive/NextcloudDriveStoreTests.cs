using Nightware.Adapters.Dav.Files.Drive;
using Nightware.Application.Files.Drives;
using Nightware.Domain.Files;
using Nightware.Domain.Kernel;

using static Nightware.Adapters.Dav.Tests.Files.Drive.DriveAdapterHarness;

namespace Nightware.Adapters.Dav.Tests.Files.Drive;

public sealed class NextcloudDriveStoreTests : IDisposable
{
    private static readonly DriveItemRef ByPath = new(null, ["nwfix", "a.txt"]);
    private static readonly DriveItemRef Folder = new(null, ["nwfix"]);
    private readonly DriveAdapterHarness _nc = new();

    public void Dispose() => _nc.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IDriveStore Store => _nc.Get<IDriveStore>();

    [Fact]
    public void Is_registered_by_the_dav_adapter() => Store.ShouldBeOfType<NextcloudDriveStore>();

    [Fact]
    public async Task The_drive_is_the_home_with_its_quota_and_no_total_when_unlimited()
    {
        _nc.Serve("PROPFIND", Home, 207, Recorded("home-quota.propfind.xml"), "0");

        var drive = (await Store.GetDriveAsync(null, Ct)).Value;

        drive.Id.ShouldBe(DriveId);
        drive.OwnerId.ShouldBe("alice");
        drive.Quota!.Used.ShouldBeGreaterThan(0);
        drive.Quota.Remaining.ShouldBeNull();
        drive.Quota.Total.ShouldBeNull();
        _nc.Requests("PROPFIND").Single().RequestMessage!.Body!.ShouldContain("quota-available-bytes");
    }

    [Fact]
    public async Task Another_drive_is_not_found_without_asking_nextcloud()
    {
        (await Store.GetDriveAsync("nc!Ym9i", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await Store.GetItemAsync(DriveItemRef.Root with { DriveId = "nc!Ym9i" }, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _nc.Server.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_root_has_the_root_facet_and_no_parent()
    {
        _nc.ServeAliceHome();

        var root = (await Store.GetItemAsync(DriveItemRef.Root with { DriveId = DriveId }, Ct)).Value;

        root.Id.ShouldBe("123");
        root.IsRoot.ShouldBeTrue();
        root.IsFolder.ShouldBeTrue();
        root.ParentId.ShouldBeNull();
        root.DriveId.ShouldBe(DriveId);
    }

    [Fact]
    public async Task An_item_by_id_is_found_by_search_and_gets_its_parent()
    {
        _nc.ServeAliceHome();

        var item = (await Store.GetItemAsync(DriveItemRef.ById("1748"), Ct)).Value;

        item.ShouldBe(item with { Id = "1748", Name = "a.txt", Size = 5, MimeType = "text/plain", ParentId = "1746", ParentPath = "/nwfix", IsFolder = false });
        item.ETag.ShouldNotBeNullOrEmpty();
        item.CTag.ShouldBe(item.ETag);
        var search = _nc.Requests("SEARCH").Single().RequestMessage!.Body!;
        search.ShouldContain("<d:href>/files/alice</d:href>");
        search.ShouldContain("http://nextcloud.org/ns");
    }

    [Fact]
    public async Task An_item_by_path_is_a_propfind_below_the_anchor()
    {
        _nc.ServeAliceHome();

        var item = (await Store.GetItemAsync(ByPath, Ct)).Value;

        item.Id.ShouldBe("1748");
        item.ParentPath.ShouldBe("/nwfix");
        _nc.Requests("SEARCH").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    public async Task An_id_that_is_no_file_id_is_not_found_without_a_search(string id)
    {
        (await Store.GetItemAsync(DriveItemRef.ById(id), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _nc.Requests("SEARCH").ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unknown_id_is_not_found()
    {
        _nc.ServeAliceHome();
        _nc.Serve("SEARCH", DavRoot, 207, EmptyMultistatus, bodyContains: "<d:literal>999</d:literal>");

        (await Store.GetItemAsync(DriveItemRef.ById("999"), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task The_roots_own_id_is_an_anchor_although_search_never_returns_it()
    {
        _nc.ServeAliceHome();
        _nc.Serve("SEARCH", DavRoot, 207, EmptyMultistatus, bodyContains: "<d:literal>123</d:literal>");

        (await Store.GetItemAsync(DriveItemRef.ById("123"), Ct)).Value.IsRoot.ShouldBeTrue();
    }

    [Fact]
    public async Task Children_are_sorted_and_paged_with_a_skip_token()
    {
        _nc.ServeAliceHome();

        var first = (await Store.ListChildrenAsync(Folder, new DriveListQuery(1, null), Ct)).Value;
        var second = (await Store.ListChildrenAsync(Folder, new DriveListQuery(1, first.SkipToken), Ct)).Value;
        var bySizeDescending = (await Store.ListChildrenAsync(Folder, new DriveListQuery(10, null, DriveSortKey.Size, true), Ct)).Value;

        first.Items.Single().Name.ShouldBe("a.txt");
        first.SkipToken.ShouldBe("1");
        second.Items.Single().Name.ShouldBe("sub");
        second.Items.Single().IsFolder.ShouldBeTrue();
        second.Items.Single().ParentId.ShouldBe("1746");
        second.SkipToken.ShouldBeNull();
        bySizeDescending.Items.Select(i => i.Name).ShouldBe(["a.txt", "sub"]);
    }

    [Fact]
    public async Task Children_of_a_file_or_with_a_bad_token_are_refused()
    {
        _nc.ServeAliceHome();

        (await Store.ListChildrenAsync(ByPath, new DriveListQuery(5, null), Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await Store.ListChildrenAsync(Folder, new DriveListQuery(5, "x"), Ct)).Error!.Target.ShouldBe("$skiptoken");
    }

    [Fact]
    public async Task Content_streams_the_file_with_its_type_and_name()
    {
        _nc.ServeAliceHome();
        _nc.Serve("GET", Home + "nwfix/a.txt", 200, "hello", contentType: "text/plain");

        var content = (await Store.OpenContentAsync(ByPath, Ct)).Value;
        await using var stream = await content.Open(Ct);
        using var reader = new StreamReader(stream);

        (await reader.ReadToEndAsync(Ct)).ShouldBe("hello");
        content.ContentType.ShouldBe("text/plain");
        content.FileName.ShouldBe("a.txt");
    }

    [Fact]
    public async Task A_folder_has_no_content()
    {
        _nc.ServeAliceHome();

        (await Store.OpenContentAsync(Folder, Ct)).Error!.Code.ShouldBe(DriveErrors.NotSupportedCode);
    }

    [Fact]
    public async Task Uploading_onto_a_taken_name_with_fail_is_name_already_exists()
    {
        _nc.ServeAliceHome();

        var result = await Store.WriteContentAsync(ByPath, new DriveUpload(new MemoryStream([1]), "text/plain", 1, ConflictBehavior.Fail), Ct);

        result.Error!.Code.ShouldBe(DriveErrors.NameAlreadyExistsCode);
        _nc.Requests("PUT").ShouldBeEmpty();
    }

    [Fact]
    public async Task Uploading_onto_a_taken_name_with_replace_overwrites_it()
    {
        _nc.ServeAliceHome();
        _nc.Status("PUT", Home + "nwfix/a.txt", 204);

        var item = (await Store.WriteContentAsync(ByPath, new DriveUpload(new MemoryStream("hello"u8.ToArray()), "text/plain", 5, ConflictBehavior.Replace), Ct)).Value;

        item.Id.ShouldBe("1748");
        var put = _nc.Requests("PUT").Single().RequestMessage!;
        put.Headers!.ContainsKey("If-None-Match").ShouldBeFalse();
        put.Body.ShouldBe("hello");
    }

    [Fact]
    public async Task Uploading_onto_a_taken_name_with_rename_picks_the_next_free_name()
    {
        _nc.ServeAliceHome();
        _nc.Status("PUT", Home + "nwfix/a 1.txt", 201);
        _nc.Serve("PROPFIND", Home + "nwfix/a 1.txt", 207, Recorded("file.propfind.xml").Replace("nwfix/a.txt", "nwfix/a%201.txt", StringComparison.Ordinal), "0");

        var item = (await Store.WriteContentAsync(ByPath, new DriveUpload(new MemoryStream([1]), "text/plain", 1, ConflictBehavior.Rename), Ct)).Value;

        item.Name.ShouldBe("a 1.txt");
        _nc.Requests("PUT").Single().RequestMessage!.Headers!["If-None-Match"].ShouldBe(["*"]);
    }

    [Fact]
    public async Task A_new_file_is_created_only_if_absent_and_a_short_body_is_removed()
    {
        _nc.ServeAliceHome();
        _nc.Status("PROPFIND", Home + "nwfix/new.txt", 404, "0");
        _nc.Status("PUT", Home + "nwfix/new.txt", 201);
        _nc.Status("DELETE", Home + "nwfix/new.txt", 204);

        var result = await Store.WriteContentAsync(new DriveItemRef(null, ["nwfix", "new.txt"]), new DriveUpload(new MemoryStream([1, 2]), "text/plain", 5, ConflictBehavior.Fail), Ct);

        result.Error!.Target.ShouldBe("Content-Length");
        _nc.Requests("PUT").Single().RequestMessage!.Headers!["If-None-Match"].ShouldBe(["*"]);
        _nc.Requests("DELETE").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_stale_if_match_refuses_the_write_and_the_delete()
    {
        _nc.ServeAliceHome();

        (await Store.WriteContentAsync(ByPath, new DriveUpload(Stream.Null, "text/plain", 0, ConflictBehavior.Replace) { IfMatch = "\"stale\"" }, Ct))
            .Error!.Kind.ShouldBe(ErrorKind.PreconditionFailed);
        (await Store.DeleteAsync(ByPath, "\"stale\"", Ct)).Error!.Kind.ShouldBe(ErrorKind.PreconditionFailed);
        _nc.Requests("PUT").ShouldBeEmpty();
        _nc.Requests("DELETE").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_folder_name_taken_fails_or_is_renamed()
    {
        _nc.ServeAliceHome();
        _nc.Status("MKCOL", Home + "nwfix/sub 1/", 201);
        _nc.Serve("PROPFIND", Home + "nwfix/sub 1/", 207, Recorded("folder-self.propfind.xml").Replace("alice/nwfix/", "alice/nwfix/sub%201/", StringComparison.Ordinal), "0");

        (await Store.CreateFolderAsync(Folder, "SUB", ConflictBehavior.Fail, Ct)).Error!.Code.ShouldBe(DriveErrors.NameAlreadyExistsCode);
        var renamed = (await Store.CreateFolderAsync(Folder, "sub", ConflictBehavior.Rename, Ct)).Value;

        renamed.Name.ShouldBe("sub 1");
        _nc.Requests("MKCOL").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_rename_is_a_move_that_keeps_the_folder_and_refuses_to_overwrite()
    {
        _nc.ServeAliceHome();
        _nc.Status("MOVE", Home + "nwfix/a.txt", 201);
        _nc.Serve("PROPFIND", Home + "nwfix/b.txt", 207, Recorded("file.propfind.xml").Replace("nwfix/a.txt", "nwfix/b.txt", StringComparison.Ordinal), "0");

        var item = (await Store.UpdateAsync(ByPath, new DriveItemChange("b.txt", null, ConflictBehavior.Fail), Ct)).Value;

        item.Name.ShouldBe("b.txt");
        var move = _nc.Requests("MOVE").Single().RequestMessage!;
        move.Headers!["Destination"].Single().ShouldEndWith("/cloud/remote.php/dav/files/alice/nwfix/b.txt");
        move.Headers["Overwrite"].ShouldBe(["F"]);
    }

    [Fact]
    public async Task A_taken_destination_on_move_is_name_already_exists()
    {
        _nc.ServeAliceHome();
        _nc.Status("MOVE", Home + "nwfix/a.txt", 412);

        (await Store.UpdateAsync(ByPath, new DriveItemChange("b.txt", null, ConflictBehavior.Fail), Ct)).Error!.Code.ShouldBe(DriveErrors.NameAlreadyExistsCode);
    }

    [Fact]
    public async Task A_folder_cannot_move_into_itself()
    {
        _nc.ServeAliceHome();
        _nc.Serve("PROPFIND", Home + "nwfix/sub", 207, Recorded("folder-self.propfind.xml").Replace("alice/nwfix/", "alice/nwfix/sub/", StringComparison.Ordinal), "0");

        (await Store.UpdateAsync(Folder, new DriveItemChange(null, new DriveItemRef(null, ["nwfix", "sub"]), ConflictBehavior.Fail), Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        _nc.Requests("MOVE").ShouldBeEmpty();
    }

    [Fact]
    public async Task The_root_is_never_deleted_and_an_item_goes_by_delete()
    {
        _nc.ServeAliceHome();
        _nc.Status("DELETE", Home + "nwfix/a.txt", 204);

        (await Store.DeleteAsync(DriveItemRef.Root, null, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        (await Store.DeleteAsync(ByPath, null, Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_copy_beside_itself_under_the_same_name_is_renamed_and_copies_the_whole_tree()
    {
        _nc.ServeAliceHome();
        _nc.Status("COPY", Home + "nwfix/a.txt", 201);
        _nc.Serve("PROPFIND", Home + "nwfix/a 1.txt", 207, Recorded("file.propfind.xml").Replace("nwfix/a.txt", "nwfix/a%201.txt", StringComparison.Ordinal), "0");

        var copy = (await Store.CopyAsync(ByPath, null, null, ConflictBehavior.Replace, Ct)).Value;

        copy.Name.ShouldBe("a 1.txt");
        var request = _nc.Requests("COPY").Single().RequestMessage!;
        request.Headers!["Depth"].ShouldBe(["infinity"]);
        request.Headers["Overwrite"].ShouldBe(["F"]);
    }

    [Fact]
    public async Task Search_matches_names_literally_and_pages()
    {
        _nc.ServeAliceHome();
        _nc.Serve("SEARCH", DavRoot, 207, Recorded("search-by-id.xml"), bodyContains: "<d:literal>%50a%</d:literal>");

        var page = (await Store.SearchAsync("50%a", new DriveListQuery(1, null), Ct)).Value;

        page.Items.Single().Id.ShouldBe("1748");
        page.SkipToken.ShouldBeNull();
        var body = _nc.Requests("SEARCH").First().RequestMessage!.Body!;
        body.ShouldContain("<d:nresults>2</d:nresults>");
        body.ShouldContain("firstresult>0<");
    }

    [Fact]
    public async Task Recent_is_the_newest_files_by_modification_time()
    {
        _nc.ServeAliceHome();
        _nc.Serve("SEARCH", DavRoot, 207, Recorded("search-by-id.xml"), bodyContains: "<d:is-collection");

        var items = (await Store.RecentAsync(5, Ct)).Value;

        items.Single().Id.ShouldBe("1748");
        var body = _nc.Requests("SEARCH").First(e => e.RequestMessage!.Body!.Contains("is-collection", StringComparison.Ordinal)).RequestMessage!.Body!;
        body.ShouldContain("<d:not><d:is-collection /></d:not>");
        body.ShouldContain("<d:getlastmodified />");
        body.ShouldContain("<d:descending />");
        body.ShouldContain("<d:nresults>5</d:nresults>");
    }

    [Fact]
    public async Task Special_folders_are_made_on_demand()
    {
        _nc.Status("MKCOL", Home + "Apps/", 405);
        _nc.Status("MKCOL", Home + "Apps/nightmail/", 201);
        _nc.Serve("PROPFIND", Home + "Apps/nightmail/", 207, Recorded("folder-self.propfind.xml").Replace("alice/nwfix/", "alice/Apps/nightmail/", StringComparison.Ordinal), "0");
        _nc.Serve("PROPFIND", Home + "Apps/", 207, Recorded("folder-self.propfind.xml").Replace("alice/nwfix/", "alice/Apps/", StringComparison.Ordinal), "0");

        var folder = (await Store.GetSpecialFolderAsync(SpecialFolder.AppRoot, "nightmail", Ct)).Value;

        folder.Name.ShouldBe("nightmail");
        folder.ParentPath.ShouldBe("/Apps");
    }

    [Fact]
    public async Task An_item_without_a_preview_has_no_thumbnail()
    {
        _nc.ServeAliceHome();
        _nc.Serve("PROPFIND", Home + "nwfix/a.txt", 207, Recorded("file.propfind.xml").Replace("<nc:has-preview>true", "<nc:has-preview>false", StringComparison.Ordinal), "0");

        (await Store.OpenThumbnailAsync(ByPath, ThumbnailSize.Small, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task A_thumbnail_is_nextclouds_preview_at_the_sizes_edge()
    {
        _nc.ServeAliceHome();
        _nc.Serve("GET", "/cloud/index.php/core/preview", 200, "png", contentType: "image/png");

        var thumbnail = (await Store.OpenThumbnailAsync(ByPath, ThumbnailSize.Medium, Ct)).Value;

        thumbnail.ContentType.ShouldBe("image/png");
        var query = _nc.Requests("GET").Single().RequestMessage!.Query!;
        query["fileId"].ShouldBe(["1748"]);
        query["x"].ShouldBe(["176"]);
    }

    [Theory]
    [InlineData("a.txt", new string[0], "a.txt")]
    [InlineData("a.txt", new[] { "A.TXT" }, "a 1.txt")]
    [InlineData("a.txt", new[] { "a.txt", "a 1.txt" }, "a 2.txt")]
    [InlineData("Reports", new[] { "reports" }, "Reports 1")]
    [InlineData(".bashrc", new[] { ".bashrc" }, ".bashrc 1")]
    public void A_free_name_numbers_the_stem(string name, string[] taken, string expected) =>
        DriveItemLocator.FreeName(name, new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase)).ShouldBe(expected);
}

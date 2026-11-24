using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;

using Nightware.Api.Tests.Support;
using Nightware.Application.Common.Identity;
using Nightware.Application.Files.Drives;
using Nightware.Domain.Files;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Tests.Features.Files;

/// <summary>Phase 20: the drive routes through the real composition, with the drive ports faked (ADR 0008).</summary>
public sealed class DriveEndpointsTests : IAsyncLifetime
{
    private const string Site = "https://cloud.example.com/cloud";
    private const string DriveId = "nc!bWU";
    private static readonly DriveItem File = new("42", "notes.txt") { Size = 5, MimeType = "text/plain", ETag = "e1", CTag = "e1", ParentId = "7", ParentPath = "/Docs", DriveId = DriveId };
    private static readonly DriveItem Folder = new("7", "Docs") { IsFolder = true, ChildCount = 1, ParentId = "1", ParentPath = "/", DriveId = DriveId };
    private static readonly DriveItem Root = new("1", "root") { IsFolder = true, IsRoot = true, DriveId = DriveId };

    private readonly IDriveStore _store = Substitute.For<IDriveStore>();
    private readonly IDriveDelta _delta = Substitute.For<IDriveDelta>();
    private readonly IDriveUploads _uploads = Substitute.For<IDriveUploads>();
    private readonly IDriveSharing _sharing = Substitute.For<IDriveSharing>();
    private readonly IDriveVersions _versions = Substitute.For<IDriveVersions>();
    private readonly IMailboxResolver _mailboxes = Substitute.For<IMailboxResolver>();
    private TestHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient Client => _host.AuthorizedClient();

    public async ValueTask InitializeAsync()
    {
        _mailboxes.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(c => new MailboxRef(c.ArgAt<string?>(0) ?? TestHost.UserEmail));
        _host = await TestHost.StartAsync(
            services =>
            {
                services.AddScoped(_ => _store);
                services.AddScoped(_ => _delta);
                services.AddScoped(_ => _uploads);
                services.AddScoped(_ => _sharing);
                services.AddScoped(_ => _versions);
                services.AddScoped(_ => _mailboxes);
            },
            configuration: new Dictionary<string, string?> { ["Discovery:FilesBaseUrls:0"] = Site });
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static DriveItemRef Ref(DriveItemRef expected) =>
        Arg.Is<DriveItemRef>(r => r.ItemId == expected.ItemId && r.Path.SequenceEqual(expected.Path) && r.DriveId == expected.DriveId);

    private HttpClient Anonymous()
    {
        var client = _host.AuthorizedClient();
        client.DefaultRequestHeaders.Authorization = null;
        return client;
    }

    private static string Local(Uri absolute) => absolute.AbsoluteUri.Replace("https://graph.test.example", string.Empty, StringComparison.Ordinal);

    private static string Local(string absolute) => Local(new Uri(absolute));

    private async Task<JsonObject> GetAsync(string path, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await Client.GetAsync(path, Ct);
        response.StatusCode.ShouldBe(expected, await response.Content.ReadAsStringAsync(Ct));
        return await response.ReadObjectAsync();
    }

    [Fact]
    public async Task The_drive_is_personal_with_its_owner_quota_and_web_url()
    {
        _store.GetDriveAsync(null, Arg.Any<CancellationToken>()).Returns(new Drive(DriveId, "OneDrive")
        {
            OwnerId = "me",
            OwnerDisplayName = "Me Example",
            Quota = new DriveQuota(100, 900, 1000),
        });

        var drive = await GetAsync("/v1.0/me/drive");

        drive.Str("id").ShouldBe(DriveId);
        drive.Str("driveType").ShouldBe("personal");
        drive.Str("owner.user.displayName").ShouldBe("Me Example");
        drive["quota"]!["total"]!.GetValue<long>().ShouldBe(1000);
        drive.Str("quota.state").ShouldBe("normal");
        drive.Str("webUrl").ShouldBe(Site + "/apps/files/");
    }

    [Fact]
    public async Task Drives_lists_the_one_drive_and_a_drive_by_id_passes_the_id()
    {
        _store.GetDriveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(new Drive(DriveId, "OneDrive"));

        (await GetAsync("/v1.0/me/drives"))["value"]!.AsArray().ShouldHaveSingleItem();
        (await GetAsync($"/v1.0/drives/{DriveId}")).Str("id").ShouldBe(DriveId);
        await _store.Received().GetDriveAsync(DriveId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Another_users_drive_is_access_denied_and_ones_own_by_address_is_served()
    {
        _store.GetDriveAsync(null, Arg.Any<CancellationToken>()).Returns(new Drive(DriveId, "OneDrive"));

        (await GetAsync("/v1.0/users/bob@example.com/drive", HttpStatusCode.Forbidden)).Str("error.code").ShouldBe("accessDenied");
        (await GetAsync($"/v1.0/users/{TestHost.UserEmail}/drive")).Str("id").ShouldBe(DriveId);
    }

    [Fact]
    public async Task The_root_has_the_root_facet()
    {
        _store.GetItemAsync(Ref(DriveItemRef.Root), Arg.Any<CancellationToken>()).Returns(Root);

        var root = await GetAsync("/v1.0/me/drive/root");

        root["root"].ShouldNotBeNull();
        root["folder"]!["childCount"]!.GetValue<int>().ShouldBe(0);
        root.Str("parentReference.driveId").ShouldBe(DriveId);
    }

    [Fact]
    public async Task An_item_is_graph_shaped()
    {
        _store.GetItemAsync(Ref(DriveItemRef.ById("42")), Arg.Any<CancellationToken>()).Returns(File);

        var item = await GetAsync("/v1.0/me/drive/items/42");

        item.Str("name").ShouldBe("notes.txt");
        item.Str("eTag").ShouldBe("\"e1\"");
        item.Str("cTag").ShouldBe("\"c:e1\"");
        item.Str("file.mimeType").ShouldBe("text/plain");
        item.Str("parentReference.id").ShouldBe("7");
        item.Str("parentReference.path").ShouldBe("/drive/root:/Docs");
        item.Str("webUrl").ShouldBe(Site + "/f/42");
        item.ContainsKey("folder").ShouldBeFalse();
        item["file"]!.AsObject().ContainsKey("hashes").ShouldBeFalse();
    }

    [Fact]
    public async Task A_file_carries_a_download_url_that_works_without_a_bearer_and_a_listing_does_not()
    {
        _store.GetItemAsync(Ref(DriveItemRef.ById("42")), Arg.Any<CancellationToken>()).Returns(File);
        _store.OpenContentAsync(Ref(DriveItemRef.ById("42")), Arg.Any<CancellationToken>())
            .Returns(new BlobContent("text/plain", 5, _ => Task.FromResult<Stream>(new MemoryStream("hello"u8.ToArray()))) { FileName = "notes.txt" });
        _store.GetItemAsync(Ref(DriveItemRef.ById("7")), Arg.Any<CancellationToken>()).Returns(Folder);
        _store.ListChildrenAsync(Arg.Any<DriveItemRef>(), Arg.Any<DriveListQuery>(), Arg.Any<CancellationToken>())
            .Returns(new Page<DriveItem>([File], null));

        var item = await GetAsync("/v1.0/me/drive/items/42");
        var url = new Uri(item["@microsoft.graph.downloadUrl"]!.GetValue<string>());
        url.AbsoluteUri.ShouldStartWith(TestHost.GraphBase + "/me/drive/items/42/$value?authtoken=");
        using var download = await Anonymous().GetAsync(Local(url), Ct);
        (await download.Content.ReadAsStringAsync(Ct)).ShouldBe("hello");

        (await GetAsync("/v1.0/me/drive/items/7")).ContainsKey("@microsoft.graph.downloadUrl").ShouldBeFalse();
        (await GetAsync("/v1.0/me/drive/items/7/children"))["value"]![0]!.AsObject().ContainsKey("@microsoft.graph.downloadUrl").ShouldBeFalse();
    }

    [Fact]
    public async Task A_files_stored_hashes_are_graphs_hashes()
    {
        _store.GetItemAsync(Ref(DriveItemRef.ById("42")), Arg.Any<CancellationToken>()).Returns(File with { Sha1Hash = "AB12" });

        var item = await GetAsync("/v1.0/me/drive/items/42");

        item.Str("file.hashes.sha1Hash").ShouldBe("AB12");
        item["file"]!["hashes"]!.AsObject().ContainsKey("sha256Hash").ShouldBeFalse();
    }

    [Theory]
    [InlineData("/v1.0/me/drive/root:/Docs/notes.txt", null, new[] { "Docs", "notes.txt" }, null)]
    [InlineData("/v1.0/me/drive/root:/Docs/notes.txt:", null, new[] { "Docs", "notes.txt" }, null)]
    [InlineData("/v1.0/me/drive/items/7:/notes.txt:", "7", new[] { "notes.txt" }, null)]
    [InlineData("/beta/drives/nc!bWU/root:/a%20b.txt", null, new[] { "a b.txt" }, DriveId)]
    public async Task Path_addressing_names_an_item_below_its_anchor(string path, string? anchor, string[] segments, string? driveId)
    {
        _store.GetItemAsync(Ref(new DriveItemRef(anchor, segments) { DriveId = driveId }), Arg.Any<CancellationToken>()).Returns(File);

        (await GetAsync(path)).Str("id").ShouldBe("42");
    }

    [Fact]
    public async Task A_path_with_a_dot_segment_is_refused()
    {
        (await GetAsync("/v1.0/me/drive/root:/Docs/..%2F..:", HttpStatusCode.BadRequest)).Str("error.code").ShouldBe("invalidRequest");
        await _store.DidNotReceiveWithAnyArgs().GetItemAsync(default!, Ct);
    }

    [Fact]
    public async Task A_missing_item_is_onedrives_item_not_found()
    {
        _store.GetItemAsync(Arg.Any<DriveItemRef>(), Arg.Any<CancellationToken>()).Returns(Error.NotFound("gone"));

        (await GetAsync("/v1.0/me/drive/items/99", HttpStatusCode.NotFound)).Str("error.code").ShouldBe("itemNotFound");
    }

    [Fact]
    public async Task Children_page_with_the_requests_options()
    {
        _store.ListChildrenAsync(Ref(DriveItemRef.ById("7")), new DriveListQuery(1, null, DriveSortKey.Size, true), Arg.Any<CancellationToken>())
            .Returns(new Page<DriveItem>([File], "1"));

        var page = await GetAsync("/v1.0/me/drive/items/7/children?$top=1&$orderby=size desc");

        page["value"]!.AsArray().ShouldHaveSingleItem();
        page["@odata.nextLink"]!.GetValue<string>().ShouldContain("$skiptoken=1");
    }

    [Theory]
    [InlineData("$filter=name eq 'a'")]
    [InlineData("$orderby=createdDateTime")]
    [InlineData("$orderby=name,size")]
    public async Task Children_refuse_options_a_drive_listing_doesnt_take(string query)
    {
        await GetAsync("/v1.0/me/drive/root/children?" + query, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Expand_children_carries_the_folders_children()
    {
        _store.GetItemAsync(Ref(DriveItemRef.ById("7")), Arg.Any<CancellationToken>()).Returns(Folder);
        _store.ListChildrenAsync(Ref(DriveItemRef.ById("7")), Arg.Any<DriveListQuery>(), Arg.Any<CancellationToken>()).Returns(new Page<DriveItem>([File]));

        var folder = await GetAsync("/v1.0/me/drive/items/7?$expand=children");

        folder["children"]!.AsArray().ShouldHaveSingleItem()!.Str("id").ShouldBe("42");
    }

    [Fact]
    public async Task A_folder_is_created_with_its_conflict_behaviour_and_a_file_is_not()
    {
        _store.CreateFolderAsync(Ref(DriveItemRef.Root), "New", ConflictBehavior.Rename, Arg.Any<CancellationToken>()).Returns(Folder);

        using var created = await Client.PostAsJsonAsync("/v1.0/me/drive/root/children",
            new Dictionary<string, object> { ["name"] = "New", ["folder"] = new { }, ["@microsoft.graph.conflictBehavior"] = "rename" }, Ct);
        using var file = await Client.PostAsJsonAsync("/v1.0/me/drive/root/children", new { name = "a.txt", file = new { } }, Ct);

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        file.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Content_redirects_to_a_url_that_works_without_a_bearer()
    {
        _store.GetItemAsync(Ref(DriveItemRef.ById("42")), Arg.Any<CancellationToken>()).Returns(File);
        _store.OpenContentAsync(Ref(DriveItemRef.ById("42")), Arg.Any<CancellationToken>())
            .Returns(new BlobContent("text/plain", 5, _ => Task.FromResult<Stream>(new MemoryStream("hello"u8.ToArray()))) { FileName = "notes.txt" });

        using var redirect = await Client.GetAsync("/v1.0/me/drive/items/42/content", Ct);
        redirect.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        var location = redirect.Headers.Location!;
        location.AbsoluteUri.ShouldStartWith(TestHost.GraphBase + "/me/drive/items/42/$value?authtoken=");

        using var download = await Anonymous().GetAsync(Local(location), Ct);
        download.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await download.Content.ReadAsStringAsync(Ct)).ShouldBe("hello");

        // The URL is bound to its item: another item's path with the same token is refused.
        using var other = await Anonymous().GetAsync(Local(location).Replace("/items/42/", "/items/43/", StringComparison.Ordinal), Ct);
        other.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_folders_content_is_not_supported()
    {
        _store.GetItemAsync(Arg.Any<DriveItemRef>(), Arg.Any<CancellationToken>()).Returns(Folder);

        (await GetAsync("/v1.0/me/drive/items/7/content", HttpStatusCode.BadRequest)).Str("error.code").ShouldBe("notSupported");
    }

    [Fact]
    public async Task A_path_upload_is_created_with_replace_by_default_and_an_id_upload_replaces()
    {
        _store.WriteContentAsync(Arg.Any<DriveItemRef>(), Arg.Any<DriveUpload>(), Arg.Any<CancellationToken>()).Returns(File);

        using var byPath = await Client.PutAsync("/v1.0/me/drive/root:/Docs/notes.txt:/content", new StringContent("hello"), Ct);
        using var byId = new HttpRequestMessage(HttpMethod.Put, "/v1.0/me/drive/items/42/content?@microsoft.graph.conflictBehavior=fail") { Content = new StringContent("hello") };
        byId.Headers.TryAddWithoutValidation("If-Match", "\"e1\"");
        using var replaced = await Client.SendAsync(byId, Ct);

        byPath.StatusCode.ShouldBe(HttpStatusCode.Created);
        replaced.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _store.Received().WriteContentAsync(Ref(new DriveItemRef(null, ["Docs", "notes.txt"])), Arg.Is<DriveUpload>(u => u.Conflict == ConflictBehavior.Replace && u.Length == 5), Arg.Any<CancellationToken>());
        await _store.Received().WriteContentAsync(Ref(DriveItemRef.ById("42")), Arg.Is<DriveUpload>(u => u.Conflict == ConflictBehavior.Fail && u.IfMatch == "\"e1\""), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unknown_conflict_behaviour_is_refused()
    {
        using var response = await Client.PutAsync("/v1.0/me/drive/root:/a.txt:/content?@microsoft.graph.conflictBehavior=merge", new StringContent("x"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Patch_renames_and_moves_by_parent_id_or_path()
    {
        _store.UpdateAsync(Arg.Any<DriveItemRef>(), Arg.Any<DriveItemChange>(), Arg.Any<CancellationToken>()).Returns(File);

        using var byId = await Client.PatchAsJsonAsync("/v1.0/me/drive/items/42", new { name = "b.txt", parentReference = new { id = "7" } }, Ct);
        using var byPath = await Client.PatchAsJsonAsync("/v1.0/me/drive/items/42", new { parentReference = new { path = "/drive/root:/Docs/Old" } }, Ct);

        byId.StatusCode.ShouldBe(HttpStatusCode.OK);
        byPath.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _store.Received().UpdateAsync(Ref(DriveItemRef.ById("42")), Arg.Is<DriveItemChange>(c => c.Name == "b.txt" && c.NewParent!.ItemId == "7"), Arg.Any<CancellationToken>());
        await _store.Received().UpdateAsync(Ref(DriveItemRef.ById("42")), Arg.Is<DriveItemChange>(c => c.Name == null && c.NewParent!.Path.SequenceEqual(new[] { "Docs", "Old" })), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_is_no_content_and_passes_if_match()
    {
        _store.DeleteAsync(Arg.Any<DriveItemRef>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        using var request = new HttpRequestMessage(HttpMethod.Delete, "/v1.0/me/drive/items/42");
        request.Headers.TryAddWithoutValidation("If-Match", "\"e1\"");

        using var response = await Client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await _store.Received().DeleteAsync(Ref(DriveItemRef.ById("42")), "\"e1\"", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Copy_is_accepted_with_a_monitor_that_reports_the_copy()
    {
        _store.CopyAsync(Ref(DriveItemRef.ById("42")), Arg.Any<DriveItemRef?>(), "copy.txt", ConflictBehavior.Fail, Arg.Any<CancellationToken>())
            .Returns(File with { Id = "43", Name = "copy.txt" });

        using var accepted = await Client.PostAsJsonAsync("/v1.0/me/drive/items/42/copy", new { parentReference = new { id = "7" }, name = "copy.txt" }, Ct);

        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var status = await (await Anonymous().GetAsync(Local(accepted.Headers.Location!), Ct)).ReadObjectAsync();
        status.Str("status").ShouldBe("completed");
        status.Str("resourceId").ShouldBe("43");
    }

    [Fact]
    public async Task An_upload_session_takes_chunks_through_its_own_url_and_can_be_read_and_cancelled()
    {
        _uploads.BeginAsync(Arg.Any<DriveUploadFile>(), Arg.Any<CancellationToken>())
            .Returns(new DriveUploadState("sess", DateTimeOffset.UtcNow.AddHours(1), 0));
        _uploads.AppendAsync("sess", 0, 5, 10, Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(new DriveUploadProgress(5, null));
        _uploads.AppendAsync("sess", 5, 5, 10, Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(new DriveUploadProgress(10, File));
        _uploads.GetAsync("sess", Arg.Any<CancellationToken>()).Returns(new DriveUploadState("sess", DateTimeOffset.UtcNow.AddHours(1), 5));
        _uploads.CancelAsync("sess", Arg.Any<CancellationToken>()).Returns(Result.Success());

        var session = await (await Client.PostAsJsonAsync("/v1.0/me/drive/root:/Docs/big.bin:/createUploadSession",
            new { item = new Dictionary<string, object> { ["@microsoft.graph.conflictBehavior"] = "rename", ["fileSize"] = 10 } }, Ct)).ReadObjectAsync();
        var uploadUrl = Local(session.Str("uploadUrl")!);

        await _uploads.Received().BeginAsync(
            Arg.Is<DriveUploadFile>(f => f.Name == "big.bin" && f.Size == 10 && f.Conflict == ConflictBehavior.Rename && f.Parent.Path.SequenceEqual(new[] { "Docs" })),
            Arg.Any<CancellationToken>());
        using var anonymous = Anonymous();
        var first = new ByteArrayContent(new byte[5]);
        first.Headers.ContentRange = new ContentRangeHeaderValue(0, 4, 10);
        using var chunk = await anonymous.PutAsync(uploadUrl, first, Ct);
        chunk.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await chunk.ReadObjectAsync())["nextExpectedRanges"]![0]!.GetValue<string>().ShouldBe("5-");

        (await (await anonymous.GetAsync(uploadUrl, Ct)).ReadObjectAsync())["nextExpectedRanges"]![0]!.GetValue<string>().ShouldBe("5-");

        var last = new ByteArrayContent(new byte[5]);
        last.Headers.ContentRange = new ContentRangeHeaderValue(5, 9, 10);
        using var done = await anonymous.PutAsync(uploadUrl, last, Ct);
        done.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await done.ReadObjectAsync()).Str("id").ShouldBe("42");

        (await anonymous.DeleteAsync(uploadUrl, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // The upload URL is the only credential: a bearer on another session's URL gets nowhere.
        (await Client.PutAsync("/v1.0/me/drive/uploadSessions/sess", new ByteArrayContent([]), Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_session_for_an_existing_file_replaces_it_in_its_folder()
    {
        _store.GetItemAsync(Ref(DriveItemRef.ById("42")), Arg.Any<CancellationToken>()).Returns(File);
        _uploads.BeginAsync(Arg.Any<DriveUploadFile>(), Arg.Any<CancellationToken>()).Returns(new DriveUploadState("s", DateTimeOffset.UtcNow.AddHours(1), 0));

        using var response = await Client.PostAsJsonAsync("/v1.0/me/drive/items/42/createUploadSession", new { }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await _uploads.Received().BeginAsync(Arg.Is<DriveUploadFile>(f => f.Parent.ItemId == "7" && f.Name == "notes.txt" && f.Conflict == ConflictBehavior.Replace), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Thumbnails_are_one_set_of_self_authorising_urls_or_none()
    {
        _store.GetItemAsync(Ref(DriveItemRef.ById("42")), Arg.Any<CancellationToken>()).Returns(File with { HasThumbnail = true });
        _store.GetItemAsync(Ref(DriveItemRef.ById("43")), Arg.Any<CancellationToken>()).Returns(File with { Id = "43" });
        _store.OpenThumbnailAsync(Ref(DriveItemRef.ById("42")), ThumbnailSize.Large, Arg.Any<CancellationToken>())
            .Returns(new BlobContent("image/png", 3, _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3]))));

        (await GetAsync("/v1.0/me/drive/items/43/thumbnails"))["value"]!.AsArray().ShouldBeEmpty();
        var set = (await GetAsync("/v1.0/me/drive/items/42/thumbnails"))["value"]!.AsArray().ShouldHaveSingleItem()!;
        set["small"]!["width"]!.GetValue<int>().ShouldBe(96);

        using var image = await Anonymous().GetAsync(Local(set.Str("large.url")!), Ct);
        image.StatusCode.ShouldBe(HttpStatusCode.OK);
        image.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");

        using var direct = await Client.GetAsync("/v1.0/me/drive/items/42/thumbnails/0/large/content", Ct);
        direct.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Client.GetAsync("/v1.0/me/drive/items/42/thumbnails/1/large/content", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_preview_is_the_large_rendered_preview_never_the_file()
    {
        _store.GetItemAsync(Ref(DriveItemRef.ById("42")), Arg.Any<CancellationToken>()).Returns(File with { HasThumbnail = true, MimeType = "text/html" });
        _store.GetItemAsync(Ref(DriveItemRef.ById("43")), Arg.Any<CancellationToken>()).Returns(File with { Id = "43" });
        _store.OpenThumbnailAsync(Ref(DriveItemRef.ById("42")), ThumbnailSize.Large, Arg.Any<CancellationToken>())
            .Returns(new BlobContent("image/png", 3, _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3]))));

        using var created = await Client.PostAsJsonAsync("/v1.0/me/drive/items/42/preview", new { viewer = "onedrive" }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.OK);
        var url = (await created.ReadObjectAsync()).Str("getUrl")!;
        using var image = await Anonymous().GetAsync(Local(url), Ct);
        image.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
        await _store.DidNotReceiveWithAnyArgs().OpenContentAsync(default!, Ct);

        using var none = await Client.PostAsync("/v1.0/me/drive/items/43/preview", null, Ct);
        none.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await none.ReadObjectAsync()).Str("error.code").ShouldBe("notSupported");
    }

    [Fact]
    public async Task Sharing_creates_links_invites_lists_and_removes_permissions()
    {
        var link = new DriveShare("41", ["read"]) { Link = new SharingLink("view", "anonymous", "https://cloud.example.com/s/abc") };
        var invite = new DriveShare("42", ["write"]) { GrantedTo = new EmailAddress("bob@example.com", "Bob") };
        _sharing.CreateLinkAsync(Ref(DriveItemRef.ById("42")), new SharingLinkRequest("view", null, null), Arg.Any<CancellationToken>()).Returns(link);
        _sharing.InviteAsync(Ref(DriveItemRef.ById("42")), Arg.Any<DriveInvitation>(), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<DriveShare>>.Success([invite]));
        _sharing.ListPermissionsAsync(Ref(DriveItemRef.ById("42")), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<DriveShare>>.Success([link, invite]));
        _sharing.DeletePermissionAsync(Ref(DriveItemRef.ById("42")), "41", Arg.Any<CancellationToken>()).Returns(Result.Success());

        using var created = await Client.PostAsJsonAsync("/v1.0/me/drive/items/42/createLink", new { type = "view", scope = "anonymous" }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var createdLink = await created.ReadObjectAsync();
        createdLink.Str("link.webUrl").ShouldBe("https://cloud.example.com/s/abc");
        createdLink["link"]!["preventsDownload"]!.GetValue<bool>().ShouldBeFalse();

        using var invited = await Client.PostAsJsonAsync("/v1.0/me/drive/items/42/invite",
            new { recipients = new[] { new { email = "bob@example.com" } }, roles = new[] { "Write" }, sendInvitation = false }, Ct);
        (await invited.ReadObjectAsync())["value"]![0].Str("grantedToV2.user.email").ShouldBe("bob@example.com");
        await _sharing.Received().InviteAsync(Arg.Any<DriveItemRef>(), Arg.Is<DriveInvitation>(i => i.Recipients.Single() == "bob@example.com" && i.Roles.Single() == "write"), Arg.Any<CancellationToken>());

        (await GetAsync("/v1.0/me/drive/items/42/permissions"))["value"]!.AsArray().Count.ShouldBe(2);
        (await Client.DeleteAsync("/v1.0/me/drive/items/42/permissions/41", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Shared_with_me_points_at_each_item_with_its_owner()
    {
        var shared = File with { Id = "557", Name = "plan.txt", ParentId = null, ParentPath = "/", SharedBy = new DriveItemSharer("bob", "Bob Brown", new DateTimeOffset(2026, 11, 24, 5, 0, 0, TimeSpan.Zero)) };
        _sharing.SharedWithMeAsync(Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<DriveItem>>.Success([shared]));

        var entry = (await GetAsync("/v1.0/me/drive/sharedWithMe"))["value"]![0]!.AsObject();

        entry.Str("id").ShouldBe("557");
        entry.Str("remoteItem.id").ShouldBe("557");
        entry.Str("remoteItem.name").ShouldBe("plan.txt");
        entry.Str("remoteItem.parentReference.driveId").ShouldBe(DriveId);
        entry.Str("remoteItem.file.mimeType").ShouldBe("text/plain");
        entry.Str("remoteItem.shared.owner.user.displayName").ShouldBe("Bob Brown");
        entry.Str("remoteItem.shared.sharedDateTime").ShouldBe("2026-11-24T05:00:00Z");
        entry.Str("remoteItem.webUrl").ShouldBe(Site + "/f/557");
    }

    [Fact]
    public async Task Recent_lists_files_as_remote_items_without_an_owner()
    {
        _store.RecentAsync(100, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<DriveItem>>.Success([File]));

        var entry = (await GetAsync("/v1.0/me/drive/recent"))["value"]![0]!.AsObject();

        entry.Str("remoteItem.id").ShouldBe("42");
        entry["remoteItem"]!.AsObject().ContainsKey("shared").ShouldBeFalse();
    }

    [Fact]
    public async Task Someone_elses_shared_with_me_and_recent_are_refused()
    {
        using var shared = await Client.GetAsync("/v1.0/users/bob@example.com/drive/sharedWithMe", Ct);
        using var recent = await Client.GetAsync("/v1.0/drives/nc!Ym9i/recent", Ct);

        shared.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        recent.StatusCode.ShouldNotBe(HttpStatusCode.OK);
        await _sharing.DidNotReceiveWithAnyArgs().SharedWithMeAsync(Ct);
        await _store.DidNotReceiveWithAnyArgs().RecentAsync(default, Ct);
    }

    [Fact]
    public async Task Microsoft_search_finds_drive_items_by_name()
    {
        _store.SearchAsync("notes", new DriveListQuery(50, null), Arg.Any<CancellationToken>()).Returns(new Page<DriveItem>([File], null));

        using var response = await Client.PostAsJsonAsync("/v1.0/search/query",
            new { requests = new[] { new { entityTypes = new[] { "driveItem" }, query = new { queryString = "notes" }, from = 0, size = 50 } } }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var result = (await response.ReadObjectAsync())["value"]![0]!;
        result["searchTerms"]![0]!.GetValue<string>().ShouldBe("notes");
        var container = result["hitsContainers"]![0]!;
        container["moreResultsAvailable"]!.GetValue<bool>().ShouldBeFalse();
        var hit = container["hits"]![0]!;
        hit["hitId"]!.GetValue<string>().ShouldBe("42");
        hit["rank"]!.GetValue<int>().ShouldBe(1);
        hit["resource"]!["@odata.type"]!.GetValue<string>().ShouldBe("#microsoft.graph.driveItem");
        hit["resource"]!["name"]!.GetValue<string>().ShouldBe("notes.txt");
        hit["resource"]!["parentReference"]!["driveId"]!.GetValue<string>().ShouldBe(DriveId);
    }

    [Theory]
    [InlineData("""{"requests":[{"entityTypes":["message"],"query":{"queryString":"x"}}]}""", "notSupported")]
    [InlineData("""{"requests":[{"entityTypes":["driveItem","listItem"],"query":{"queryString":"x"}}]}""", "notSupported")]
    [InlineData("""{"requests":[{"entityTypes":["driveItem"],"query":{}}]}""", "invalidRequest")]
    [InlineData("""{"requests":[]}""", "invalidRequest")]
    [InlineData("""{"requests":[{"entityTypes":["driveItem"],"query":{"queryString":"x"},"size":"ten"}]}""", "invalidRequest")]
    public async Task Microsoft_search_takes_one_drive_item_request(string body, string code)
    {
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var response = await Client.PostAsync("/v1.0/search/query", content, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadObjectAsync()).Str("error.code").ShouldBe(code);
        await _store.DidNotReceiveWithAnyArgs().SearchAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task A_files_versions_are_listed_read_and_restored()
    {
        var versions = _versions;
        var older = new DriveItemVersion("1795498534") { Size = 8, LastModifiedDateTime = new DateTimeOffset(2026, 11, 24, 5, 35, 34, TimeSpan.Zero), ModifiedBy = "alice" };
        versions.ListAsync(Ref(DriveItemRef.ById("42")), Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<DriveItemVersion>>.Success([older]));
        versions.OpenContentAsync(Ref(DriveItemRef.ById("42")), "1795498534", Arg.Any<CancellationToken>())
            .Returns(new BlobContent("text/plain", 8, _ => Task.FromResult<Stream>(new MemoryStream("hello v1"u8.ToArray()))));
        versions.RestoreAsync(Ref(DriveItemRef.ById("42")), "1795498534", Arg.Any<CancellationToken>()).Returns(Result.Success());

        var listed = (await GetAsync("/v1.0/me/drive/items/42/versions"))["value"]![0]!.AsObject();
        listed.Str("id").ShouldBe("1795498534");
        listed.Str("lastModifiedBy.user.id").ShouldBe("alice");
        listed["size"]!.GetValue<long>().ShouldBe(8);
        (await GetAsync("/v1.0/me/drive/items/42/versions/1795498534")).Str("lastModifiedDateTime").ShouldBe("2026-11-24T05:35:34Z");
        (await Client.GetStringAsync("/v1.0/me/drive/items/42/versions/1795498534/content", Ct)).ShouldBe("hello v1");
        using var restored = await Client.PostAsync("/v1.0/me/drive/items/42/versions/1795498534/restoreVersion", null, Ct);
        restored.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var missing = await Client.GetAsync("/v1.0/me/drive/items/42/versions/7", Ct);
        missing.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_file_is_checked_out_and_back_in_with_a_comment()
    {
        _versions.CheckOutAsync(Ref(DriveItemRef.ById("42")), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _versions.CheckInAsync(Ref(DriveItemRef.ById("42")), "ready", Arg.Any<CancellationToken>()).Returns(Result.Success());

        using var checkedOut = await Client.PostAsync("/v1.0/me/drive/items/42/checkout", null, Ct);
        using var checkedIn = await Client.PostAsJsonAsync("/v1.0/me/drive/items/42/checkin", new { checkInAs = "published", comment = "ready" }, Ct);

        checkedOut.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        checkedIn.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        await _versions.Received().CheckInAsync(Arg.Any<DriveItemRef>(), "ready", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_file_checked_out_by_someone_else_is_a_conflict_and_shows_who()
    {
        _versions.CheckOutAsync(Arg.Any<DriveItemRef>(), Arg.Any<CancellationToken>()).Returns(Result.Failure(new Error(ErrorKind.Conflict, "resourceLocked", "checked out")));
        _store.GetItemAsync(Ref(DriveItemRef.ById("42")), Arg.Any<CancellationToken>()).Returns(File with { CheckedOutBy = new DriveCheckout("bob", "Bob Brown") });

        using var refused = await Client.PostAsync("/v1.0/me/drive/items/42/checkout", null, Ct);
        var item = await GetAsync("/v1.0/me/drive/items/42");

        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await refused.ReadObjectAsync()).Str("error.code").ShouldBe("resourceLocked");
        item.Str("publication.level").ShouldBe("checkout");
        item.Str("publication.checkedOutBy.user.displayName").ShouldBe("Bob Brown");
    }

    [Fact]
    public async Task Delta_lists_items_and_deletions_and_hands_out_a_delta_link()
    {
        _delta.ChangesAsync(null, Arg.Any<int>(), false, Arg.Any<CancellationToken>())
            .Returns(DeltaPage<DriveItem>.Done([new DeltaEntry<DriveItem>.Upsert(Root), new DeltaEntry<DriveItem>.Upsert(DriveItem.Deleted("9"))], new DeltaToken("t1")));

        var page = await GetAsync("/v1.0/me/drive/root/delta");

        page["value"]![0]!["root"].ShouldNotBeNull();
        page["value"]![1]!.AsObject().Select(p => p.Key).ShouldBe(["id", "deleted"]);
        page["@odata.deltaLink"]!.GetValue<string>().ShouldContain("$deltatoken=");
    }

    [Fact]
    public async Task Delta_token_latest_asks_for_a_token_only_and_any_other_token_is_refused()
    {
        _delta.ChangesAsync(null, Arg.Any<int>(), true, Arg.Any<CancellationToken>()).Returns(DeltaPage<DriveItem>.Done([], new DeltaToken("t")));

        (await GetAsync("/v1.0/me/drive/root/delta?token=latest"))["value"]!.AsArray().ShouldBeEmpty();
        await GetAsync("/v1.0/me/drive/root/delta?token=2026-01-01", HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Search_unescapes_the_odata_literal()
    {
        _store.SearchAsync("it's", Arg.Any<DriveListQuery>(), Arg.Any<CancellationToken>()).Returns(new Page<DriveItem>([File]));

        (await GetAsync("/v1.0/me/drive/root/search(q='it''s')"))["value"]!.AsArray().ShouldHaveSingleItem();
        (await GetAsync("/v1.0/me/drive/search(q='it''s')"))["value"]!.AsArray().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_special_folder_is_served_by_name()
    {
        _store.GetSpecialFolderAsync(SpecialFolder.Documents, Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Folder);

        (await GetAsync("/v1.0/me/drive/special/documents")).Str("id").ShouldBe("7");
        await GetAsync("/v1.0/me/drive/special/recordings", HttpStatusCode.NotFound);
    }
}

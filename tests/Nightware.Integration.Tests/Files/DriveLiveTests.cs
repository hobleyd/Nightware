using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using Nightware.Integration.Tests.Directory;
using Nightware.Integration.Tests.Support;

namespace Nightware.Integration.Tests.Files;

/// <summary>Phase 20 against the dev stack: alice's OneDrive over her Nextcloud home (ADR 0008).</summary>
public sealed class DriveLiveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonObject> SendAsync(HttpClient client, HttpMethod method, string path, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        using var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, $"{method} {path}: {text}");
        return text.Length == 0 ? [] : JsonNode.Parse(text)!.AsObject();
    }

    private static async Task<JsonObject> PutBytesAsync(HttpClient client, string path, byte[] bytes, HttpStatusCode expected, string type = "text/plain")
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        using var response = await client.PutAsync(path, content, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, $"PUT {path}: {text}");
        return JsonNode.Parse(text)!.AsObject();
    }

    /// <summary>Follows a 302 to a self-authorising URL with no bearer, as Graph clients fetch a download URL.</summary>
    private static async Task<byte[]> DownloadAsync(LiveStack stack, HttpClient client, string path)
    {
        using var redirect = await client.GetAsync(path, Ct);
        redirect.StatusCode.ShouldBe(HttpStatusCode.Found, await redirect.Content.ReadAsStringAsync(Ct));
        using var anonymous = await stack.GraphClientAsync("alice", Ct);
        anonymous.DefaultRequestHeaders.Authorization = null;
        return await anonymous.GetByteArrayAsync(LiveStack.Local(redirect.Headers.Location!.AbsoluteUri), Ct);
    }

    private static string Id(JsonObject item) => item["id"]!.GetValue<string>();

    [Fact]
    public async Task Alice_browses_writes_moves_copies_and_deletes_in_her_drive()
    {
        Assert.SkipUnless(DevStack.Enabled, "Set NIGHTWARE_INTEGRATION=1 with the dev stack running.");
        await using var stack = LiveStack.Start();
        using var alice = await stack.GraphClientAsync("alice", Ct);

        var drive = await SendAsync(alice, HttpMethod.Get, "/v1.0/me/drive");
        drive["driveType"]!.GetValue<string>().ShouldBe("personal");
        drive["quota"]!["used"]!.GetValue<long>().ShouldBeGreaterThan(0);
        var driveId = drive["id"]!.GetValue<string>();
        (await SendAsync(alice, HttpMethod.Get, $"/v1.0/drives/{driveId}"))["id"]!.GetValue<string>().ShouldBe(driveId);
        await SendAsync(alice, HttpMethod.Get, "/v1.0/drives/nc!Ym9i", expected: HttpStatusCode.NotFound);

        var root = await SendAsync(alice, HttpMethod.Get, "/v1.0/me/drive/root");
        root["root"].ShouldNotBeNull();

        var folderName = "nw-drive-" + Guid.NewGuid().ToString("N")[..8];
        var folder = await SendAsync(alice, HttpMethod.Post, "/v1.0/me/drive/root/children",
            new Dictionary<string, object> { ["name"] = folderName, ["folder"] = new { }, ["@microsoft.graph.conflictBehavior"] = "fail" }, HttpStatusCode.Created);
        var folderId = Id(folder);
        try
        {
            folder["parentReference"]!["id"]!.GetValue<string>().ShouldBe(Id(root));
            await SendAsync(alice, HttpMethod.Post, "/v1.0/me/drive/root/children",
                new Dictionary<string, object> { ["name"] = folderName, ["folder"] = new { }, ["@microsoft.graph.conflictBehavior"] = "fail" }, HttpStatusCode.Conflict);

            // Simple upload by path, then read it back by path, by id and through the download redirect.
            var bytes = Encoding.UTF8.GetBytes("hello from the façade\n");
            var file = await PutBytesAsync(alice, $"/v1.0/me/drive/root:/{folderName}/notes.txt:/content", bytes, HttpStatusCode.Created);
            file["size"]!.GetValue<long>().ShouldBe(bytes.Length);
            file["parentReference"]!["path"]!.GetValue<string>().ShouldBe($"/drive/root:/{folderName}");

            // Microsoft Search's driveItem search, as the cloud picker asks it, finds the new folder by name.
            var searched = await SendAsync(alice, HttpMethod.Post, "/v1.0/search/query",
                new { requests = new[] { new { entityTypes = new[] { "driveItem" }, query = new { queryString = folderName }, from = 0, size = 50 } } });
            searched["value"]![0]!["hitsContainers"]![0]!["hits"]!.AsArray().Select(h => h!["resource"]!["id"]!.GetValue<string>()).ShouldContain(folderId);
            var fileId = Id(file);
            Id(await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/drive/root:/{folderName}/notes.txt")).ShouldBe(fileId);
            Id(await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/drive/items/{folderId}:/notes.txt:")).ShouldBe(fileId);
            (await DownloadAsync(stack, alice, $"/v1.0/me/drive/items/{fileId}/content")).ShouldBe(bytes);

            // Conflict behaviours on upload: fail, rename.
            await SendAsync(alice, HttpMethod.Put, $"/v1.0/me/drive/root:/{folderName}/notes.txt:/content?@microsoft.graph.conflictBehavior=fail", expected: HttpStatusCode.Conflict);
            var renamedUpload = await PutBytesAsync(alice, $"/v1.0/me/drive/root:/{folderName}/notes.txt:/content?@microsoft.graph.conflictBehavior=rename", bytes, HttpStatusCode.Created);
            renamedUpload["name"]!.GetValue<string>().ShouldBe("notes 1.txt");

            var children = await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/drive/items/{folderId}/children?$orderby=name");
            children["value"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()).ShouldBe(["notes 1.txt", "notes.txt"]);
            var expanded = await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/drive/items/{folderId}?$expand=children");
            expanded["children"]!.AsArray().Count.ShouldBe(2);
            expanded["folder"]!["childCount"]!.GetValue<int>().ShouldBe(2);

            // Rename and move into a subfolder; the id survives both.
            var sub = await SendAsync(alice, HttpMethod.Post, $"/v1.0/me/drive/items/{folderId}/children", new { name = "sub", folder = new { } }, HttpStatusCode.Created);
            var moved = await SendAsync(alice, HttpMethod.Patch, $"/v1.0/me/drive/items/{fileId}", new { name = "renamed.txt", parentReference = new { id = Id(sub) } });
            Id(moved).ShouldBe(fileId);
            moved["name"]!.GetValue<string>().ShouldBe("renamed.txt");
            moved["parentReference"]!["path"]!.GetValue<string>().ShouldBe($"/drive/root:/{folderName}/sub");

            // Copy: 202 with a monitor that reports the new item.
            using (var copy = new HttpRequestMessage(HttpMethod.Post, $"/v1.0/me/drive/items/{fileId}/copy") { Content = JsonContent.Create(new { parentReference = new { id = folderId }, name = "copy.txt" }) })
            using (var copied = await alice.SendAsync(copy, Ct))
            {
                copied.StatusCode.ShouldBe(HttpStatusCode.Accepted, await copied.Content.ReadAsStringAsync(Ct));
                using var anonymous = await stack.GraphClientAsync("alice", Ct);
                anonymous.DefaultRequestHeaders.Authorization = null;
                var status = JsonNode.Parse(await anonymous.GetStringAsync(LiveStack.Local(copied.Headers.Location!.AbsoluteUri), Ct))!;
                status["status"]!.GetValue<string>().ShouldBe("completed");
                var copyItem = await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/drive/items/{status["resourceId"]!.GetValue<string>()}");
                copyItem["name"]!.GetValue<string>().ShouldBe("copy.txt");
            }

            // Search finds by name, anywhere below the root.
            var found = await SendAsync(alice, HttpMethod.Get, "/v1.0/me/drive/root/search(q='renamed')");
            found["value"]!.AsArray().Select(v => Id(v!.AsObject())).ShouldContain(fileId);

            await SendAsync(alice, HttpMethod.Delete, $"/v1.0/me/drive/items/{fileId}", expected: HttpStatusCode.NoContent);
            await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/drive/items/{fileId}", expected: HttpStatusCode.NotFound);
        }
        finally
        {
            await SendAsync(alice, HttpMethod.Delete, $"/v1.0/me/drive/items/{folderId}", expected: HttpStatusCode.NoContent);
        }
    }

    [Fact]
    public async Task A_large_file_arrives_through_an_upload_session_in_chunks()
    {
        Assert.SkipUnless(DevStack.Enabled, "Set NIGHTWARE_INTEGRATION=1 with the dev stack running.");
        await using var stack = LiveStack.Start();
        using var alice = await stack.GraphClientAsync("alice", Ct);
        var name = "nw-session-" + Guid.NewGuid().ToString("N")[..8] + ".bin";
        var file = RandomNumberGenerator.GetBytes(5 * 1024 * 1024 + 321);
        var session = await SendAsync(alice, HttpMethod.Post, $"/v1.0/me/drive/root:/{name}:/createUploadSession",
            new { item = new Dictionary<string, object> { ["@microsoft.graph.conflictBehavior"] = "fail", ["name"] = name } });
        var uploadUrl = LiveStack.Local(session["uploadUrl"]!.GetValue<string>());
        using var anonymous = await stack.GraphClientAsync("alice", Ct);
        anonymous.DefaultRequestHeaders.Authorization = null;

        const int firstChunk = 327_680 * 10;
        string? itemId = null;
        try
        {
            foreach (var (from, to, status) in new[] { (0, firstChunk - 1, HttpStatusCode.Accepted), (firstChunk, file.Length - 1, HttpStatusCode.Created) })
            {
                var content = new ByteArrayContent(file, from, to - from + 1);
                content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, file.Length);
                using var put = await anonymous.PutAsync(uploadUrl, content, Ct);
                var text = await put.Content.ReadAsStringAsync(Ct);
                put.StatusCode.ShouldBe(status, text);
                if (status == HttpStatusCode.Accepted)
                {
                    JsonNode.Parse(text)!["nextExpectedRanges"]![0]!.GetValue<string>().ShouldBe($"{firstChunk}-");
                    var progress = JsonNode.Parse(await anonymous.GetStringAsync(uploadUrl, Ct))!;
                    progress["nextExpectedRanges"]![0]!.GetValue<string>().ShouldBe($"{firstChunk}-");
                }
                else
                {
                    var item = JsonNode.Parse(text)!.AsObject();
                    item["size"]!.GetValue<long>().ShouldBe(file.Length);
                    itemId = Id(item);
                }
            }

            SHA256.HashData(await DownloadAsync(stack, alice, $"/v1.0/me/drive/items/{itemId}/content")).ShouldBe(SHA256.HashData(file));
        }
        finally
        {
            if (itemId is not null)
            {
                await SendAsync(alice, HttpMethod.Delete, $"/v1.0/me/drive/items/{itemId}", expected: HttpStatusCode.NoContent);
            }
        }
    }

    [Fact]
    public async Task Delta_reports_what_changed_since_its_link_including_deletions()
    {
        Assert.SkipUnless(DevStack.Enabled, "Set NIGHTWARE_INTEGRATION=1 with the dev stack running.");
        await using var stack = LiveStack.Start();
        using var alice = await stack.GraphClientAsync("alice", Ct);

        var latest = await SendAsync(alice, HttpMethod.Get, "/v1.0/me/drive/root/delta?token=latest");
        latest["value"]!.AsArray().ShouldBeEmpty();
        var deltaLink = LiveStack.Local(latest["@odata.deltaLink"]!.GetValue<string>());

        var name = "nw-delta-" + Guid.NewGuid().ToString("N")[..8] + ".txt";
        var file = await PutBytesAsync(alice, $"/v1.0/me/drive/root:/{name}:/content", "one"u8.ToArray(), HttpStatusCode.Created);
        var fileId = Id(file);
        var changed = await SendAsync(alice, HttpMethod.Get, deltaLink);
        changed["value"]!.AsArray().Select(v => Id(v!.AsObject())).ShouldContain(fileId);

        deltaLink = LiveStack.Local(changed["@odata.deltaLink"]!.GetValue<string>());
        await SendAsync(alice, HttpMethod.Delete, $"/v1.0/me/drive/items/{fileId}", expected: HttpStatusCode.NoContent);
        var deleted = await SendAsync(alice, HttpMethod.Get, deltaLink);
        var entry = deleted["value"]!.AsArray().Single(v => Id(v!.AsObject()) == fileId)!;
        entry["deleted"].ShouldNotBeNull();

        // An initial sync starts with the root and pages with nextLinks.
        var initial = await SendAsync(alice, HttpMethod.Get, "/v1.0/me/drive/root/delta?$top=5");
        initial["value"]!.AsArray()[0]!["root"].ShouldNotBeNull();
        initial["@odata.nextLink"].ShouldNotBeNull();
    }

    [Fact]
    public async Task Alice_shares_a_file_by_link_and_with_bob_then_takes_both_back()
    {
        Assert.SkipUnless(DevStack.Enabled, "Set NIGHTWARE_INTEGRATION=1 with the dev stack running.");
        await using var stack = LiveStack.Start();
        using var alice = await stack.GraphClientAsync("alice", Ct);
        var name = "nw-share-" + Guid.NewGuid().ToString("N")[..8] + ".txt";
        var file = await PutBytesAsync(alice, $"/v1.0/me/drive/root:/{name}:/content", "shared"u8.ToArray(), HttpStatusCode.Created);
        var fileId = Id(file);
        try
        {
            var link = await SendAsync(alice, HttpMethod.Post, $"/v1.0/me/drive/items/{fileId}/createLink", new { type = "view", scope = "anonymous" }, HttpStatusCode.Created);
            link["link"]!["webUrl"]!.GetValue<string>().ShouldContain("/s/");
            link["roles"]!.AsArray().Select(r => r!.GetValue<string>()).ShouldBe(["read"]);
            var viewOnly = await SendAsync(alice, HttpMethod.Post, $"/v1.0/me/drive/items/{fileId}/createLink", new { type = "blocksDownload" }, HttpStatusCode.Created);
            viewOnly["link"]!["type"]!.GetValue<string>().ShouldBe("blocksDownload");
            viewOnly["link"]!["preventsDownload"]!.GetValue<bool>().ShouldBeTrue();
            await SendAsync(alice, HttpMethod.Post, $"/v1.0/me/drive/items/{fileId}/createLink", new { type = "embed" }, HttpStatusCode.BadRequest);

            var bobAddress = (await SendAsync(await stack.GraphClientAsync("bob", Ct), HttpMethod.Get, "/v1.0/me"))["mail"]!.GetValue<string>();
            var invited = await SendAsync(alice, HttpMethod.Post, $"/v1.0/me/drive/items/{fileId}/invite",
                new { recipients = new[] { new { email = bobAddress } }, roles = new[] { "write" }, requireSignIn = true, sendInvitation = false });
            invited["value"]!.AsArray().ShouldHaveSingleItem()!["roles"]![0]!.GetValue<string>().ShouldBe("write");

            // Bob finds it under sharedWithMe and opens it the way the cloud picker does: through the drive its
            // remoteItem names. Alice finds her new file at the top of recent.
            using var bob = await stack.GraphClientAsync("bob", Ct);
            var entry = (await SendAsync(bob, HttpMethod.Get, "/v1.0/me/drive/sharedWithMe"))["value"]!.AsArray()
                .Single(e => e!["remoteItem"]!["id"]!.GetValue<string>() == fileId)!;
            entry["remoteItem"]!["shared"]!["owner"]!["user"]!["id"]!.GetValue<string>().ShouldBe("alice");
            var bobDrive = entry["remoteItem"]!["parentReference"]!["driveId"]!.GetValue<string>();
            (await SendAsync(bob, HttpMethod.Get, $"/v1.0/drives/{Uri.EscapeDataString(bobDrive)}/items/{fileId}"))["name"]!.GetValue<string>()
                .ShouldBe(entry["remoteItem"]!["name"]!.GetValue<string>());
            (await SendAsync(alice, HttpMethod.Get, "/v1.0/me/drive/recent"))["value"]![0]!["remoteItem"]!["id"]!.GetValue<string>().ShouldBe(fileId);

            var permissions = (await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/drive/items/{fileId}/permissions"))["value"]!.AsArray();
            permissions.Count.ShouldBe(3);
            permissions.Count(p => p!["link"]?["type"]?.GetValue<string>() == "blocksDownload").ShouldBe(1);

            // The item's own GET carries a download URL that works without a bearer.
            var item = await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/drive/items/{fileId}");
            using var anonymous = await stack.GraphClientAsync("alice", Ct);
            anonymous.DefaultRequestHeaders.Authorization = null;
            var downloaded = await anonymous.GetByteArrayAsync(LiveStack.Local(item["@microsoft.graph.downloadUrl"]!.GetValue<string>()), Ct);
            downloaded.Length.ShouldBe(item["size"]!.GetValue<int>());
            foreach (var permission in permissions)
            {
                await SendAsync(alice, HttpMethod.Delete, $"/v1.0/me/drive/items/{fileId}/permissions/{permission!["id"]!.GetValue<string>()}", expected: HttpStatusCode.NoContent);
            }

            (await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/drive/items/{fileId}/permissions"))["value"]!.AsArray().ShouldBeEmpty();
        }
        finally
        {
            await SendAsync(alice, HttpMethod.Delete, $"/v1.0/me/drive/items/{fileId}", expected: HttpStatusCode.NoContent);
        }
    }

    [Fact]
    public async Task A_replaced_file_keeps_its_old_content_as_a_version_that_can_be_restored()
    {
        Assert.SkipUnless(DevStack.Enabled, "Set NIGHTWARE_INTEGRATION=1 with the dev stack running.");
        await using var stack = LiveStack.Start();
        using var alice = await stack.GraphClientAsync("alice", Ct);

        var name = "nw-versions-" + Guid.NewGuid().ToString("N")[..8] + ".txt";
        var file = await PutBytesAsync(alice, $"/v1.0/me/drive/root:/{name}:/content", "first\n"u8.ToArray(), HttpStatusCode.Created);
        var fileId = Id(file);
        try
        {
            // Nextcloud names versions by second; a replacement within the same second would overwrite the version.
            await Task.Delay(TimeSpan.FromSeconds(1.2), Ct);
            await PutBytesAsync(alice, $"/v1.0/me/drive/items/{fileId}/content", "second\n"u8.ToArray(), HttpStatusCode.OK);

            var versions = (await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/drive/items/{fileId}/versions"))["value"]!.AsArray();
            versions.Count.ShouldBeGreaterThanOrEqualTo(2);
            var oldest = versions[^1]!["id"]!.GetValue<string>();
            (await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/drive/items/{fileId}/versions/{oldest}"))["size"]!.GetValue<long>().ShouldBe(6);
            (await alice.GetStringAsync($"/v1.0/me/drive/items/{fileId}/versions/{oldest}/content", Ct)).ShouldBe("first\n");

            await SendAsync(alice, HttpMethod.Post, $"/v1.0/me/drive/items/{fileId}/versions/{oldest}/restoreVersion", expected: HttpStatusCode.NoContent);
            Encoding.UTF8.GetString(await DownloadAsync(stack, alice, $"/v1.0/me/drive/items/{fileId}/content")).ShouldBe("first\n");
        }
        finally
        {
            await SendAsync(alice, HttpMethod.Delete, $"/v1.0/me/drive/items/{fileId}", expected: HttpStatusCode.NoContent);
        }
    }

    [Fact]
    public async Task An_image_has_thumbnails_and_special_folders_exist_on_demand()
    {
        Assert.SkipUnless(DevStack.Enabled, "Set NIGHTWARE_INTEGRATION=1 with the dev stack running.");
        await using var stack = LiveStack.Start();
        using var alice = await stack.GraphClientAsync("alice", Ct);

        var documents = await SendAsync(alice, HttpMethod.Get, "/v1.0/me/drive/special/documents");
        documents["name"]!.GetValue<string>().ShouldBe("Documents");
        documents["folder"].ShouldNotBeNull();

        // Nextcloud's skeleton files include Nextcloud.png, which has a preview.
        var image = await SendAsync(alice, HttpMethod.Get, "/v1.0/me/drive/root/search(q='Nextcloud.png')");
        var png = image["value"]!.AsArray().FirstOrDefault(v => v!["name"]!.GetValue<string>() == "Nextcloud.png");
        Assert.SkipWhen(png is null, "alice has no Nextcloud.png (the skeleton files were removed).");
        var thumbnails = await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/drive/items/{Id(png!.AsObject())}/thumbnails");
        var small = thumbnails["value"]!.AsArray().ShouldHaveSingleItem()!["small"]!["url"]!.GetValue<string>();
        using var anonymous = await stack.GraphClientAsync("alice", Ct);
        anonymous.DefaultRequestHeaders.Authorization = null;
        using var thumbnail = await anonymous.GetAsync(LiveStack.Local(small), Ct);
        thumbnail.StatusCode.ShouldBe(HttpStatusCode.OK);
        thumbnail.Content.Headers.ContentType!.MediaType.ShouldStartWith("image/");
    }
}

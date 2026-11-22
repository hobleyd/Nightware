using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Http;

using Nightware.Api.Auth;
using Nightware.Api.Features.Files.Drives;
using Nightware.Api.Features.Files.Drives.Dto;
using Nightware.Application.Common.Query;
using Nightware.Application.Files.Drives;
using Nightware.Domain.Files;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Tests.Features.Files;

/// <summary>The drive's request readers, path rewrite, URL kinds and DTOs on their own.</summary>
public sealed class DriveRequestTests
{
    [Theory]
    [InlineData("/v1.0/me/drive/root:/a/b.txt", "/v1.0/me/drive/items/root/nightware.path/", "/a/b.txt", "")]
    [InlineData("/v1.0/me/drive/root:/a/b.txt:", "/v1.0/me/drive/items/root/nightware.path/", "/a/b.txt", "")]
    [InlineData("/v1.0/me/drive/root:/a/b.txt:/content", "/v1.0/me/drive/items/root/nightware.path/", "/a/b.txt", "/content")]
    [InlineData("/beta/users/x@example.com/drive/items/42:/b.txt:/createUploadSession", "/beta/users/x@example.com/drive/items/42/nightware.path/", "/b.txt", "/createUploadSession")]
    [InlineData("/v1.0/drives/nc!YQ/root:/Docs:/children", "/v1.0/drives/nc!YQ/items/root/nightware.path/", "/Docs", "/children")]
    public void Path_addressing_is_rewritten_to_one_base64url_segment(string path, string prefix, string itemPath, string rest)
    {
        DrivePathRewriteMiddleware.TryRewrite(new PathString(path), out var rewritten).ShouldBeTrue();

        rewritten.Value!.ShouldStartWith(prefix);
        var tail = rewritten.Value![prefix.Length..];
        var segment = rest.Length == 0 ? tail : tail[..^rest.Length];
        DrivePathRewriteMiddleware.Decode(segment).ShouldBe(itemPath);
        tail.ShouldEndWith(rest);
    }

    [Theory]
    [InlineData("/v1.0/me/drive/root")]
    [InlineData("/v1.0/me/drive/items/42/content")]
    [InlineData("/v1.0/me/messages/a:b")]
    [InlineData("/healthz:/x")]
    public void Other_paths_pass_untouched(string path) =>
        DrivePathRewriteMiddleware.TryRewrite(new PathString(path), out _).ShouldBeFalse();

    [Fact]
    public void A_segment_that_isnt_base64url_text_does_not_decode()
    {
        DrivePathRewriteMiddleware.Decode(null).ShouldBeNull();
        DrivePathRewriteMiddleware.Decode("!!").ShouldBeNull();
        DrivePathRewriteMiddleware.Decode("_w").ShouldBeNull(); // 0xFF: not UTF-8
    }

    [Fact]
    public void An_item_address_reads_root_ids_and_paths()
    {
        DriveRequests.ItemRef(null, null, null).Value.IsRoot.ShouldBeTrue();
        DriveRequests.ItemRef("ROOT", null, "d").Value.ShouldSatisfyAllConditions(r => r.ItemId.ShouldBeNull(), r => r.DriveId.ShouldBe("d"));
        DriveRequests.ItemRef("42", Encode("/a/b"), null).Value.Path.ShouldBe(["a", "b"]);
        DriveRequests.ItemRef("42", Encode("/a/../b"), null).Error!.Kind.ShouldBe(ErrorKind.Validation);
        DriveRequests.ItemRef("42", "!!", null).Error!.Kind.ShouldBe(ErrorKind.Validation);
        DriveRequests.ItemRef("42", Encode("/a%2fb"), null).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Theory]
    [InlineData(null, null, ConflictBehavior.Replace)]
    [InlineData("fail", null, ConflictBehavior.Fail)]
    [InlineData("RENAME", null, ConflictBehavior.Rename)]
    [InlineData(null, "replace", ConflictBehavior.Replace)]
    [InlineData("fail", "rename", ConflictBehavior.Fail)]
    public void Conflict_behaviour_comes_from_the_query_then_the_body(string? query, string? body, ConflictBehavior expected)
    {
        var context = new DefaultHttpContext();
        if (query is not null)
        {
            context.Request.QueryString = new QueryString("?@microsoft.graph.conflictBehavior=" + query);
        }

        var json = body is null ? null : new JsonObject { ["@microsoft.graph.conflictBehavior"] = body };
        DriveRequests.Conflict(context.Request, json, ConflictBehavior.Replace).Value.ShouldBe(expected);
    }

    [Fact]
    public void A_list_query_defaults_and_maps_orderby()
    {
        DriveRequests.ListQuery(ItemQuery.Default).Value.ShouldBe(new DriveListQuery(DriveLimits.DefaultPageSize, null));
        DriveRequests.ListQuery(ItemQuery.Default with { Top = 5, SkipToken = "10", OrderBy = [new OrderBy("lastModifiedDateTime", Nightware.Application.Common.Query.SortDirection.Descending)] })
            .Value.ShouldBe(new DriveListQuery(5, "10", DriveSortKey.LastModifiedDateTime, true));
        DriveRequests.ListQuery(ItemQuery.Default with { Skip = 3 }).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public void A_parent_reference_is_an_id_or_a_root_path()
    {
        DriveRequests.ParentReference(null).Value.ShouldBeNull();
        DriveRequests.ParentReference(new JsonObject { ["parentReference"] = new JsonObject { ["id"] = "7" } }).Value!.ItemId.ShouldBe("7");
        DriveRequests.ParentReference(new JsonObject { ["parentReference"] = new JsonObject { ["path"] = "/drives/x/root:/A/B" } }).Value!.Path.ShouldBe(["A", "B"]);
        DriveRequests.ParentReference(new JsonObject { ["parentReference"] = new JsonObject { ["path"] = "/drive/root:/A/.." } }).Error!.Kind.ShouldBe(ErrorKind.Validation);
        DriveRequests.ParentReference(new JsonObject { ["parentReference"] = new JsonObject() }).Error!.Kind.ShouldBe(ErrorKind.Validation);
        DriveRequests.ParentReference(new JsonObject { ["parentReference"] = "x" }).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public void Drive_errors_carry_onedrives_codes()
    {
        Code(Error.NotFound("x")).ShouldBe("itemNotFound");
        Code(Error.Forbidden("x")).ShouldBe("accessDenied");
        Code(new Error(ErrorKind.Conflict, DriveErrors.NameAlreadyExistsCode, "x")).ShouldBe("nameAlreadyExists");
    }

    [Fact]
    public void A_deleted_item_is_its_id_and_the_deleted_facet()
    {
        var dto = DriveItemResourceDto.From(DriveItem.Deleted("9"), "https://c");

        dto.Id.ShouldBe("9");
        dto.Deleted.ShouldNotBeNull();
        dto.Name.ShouldBeNull();
        dto.ParentReference.ShouldBeNull();
    }

    [Fact]
    public void A_permission_reads_its_link_grant_and_inheritance()
    {
        var dto = PermissionDto.From(new DriveShare("9", ["read"]) { GrantedTo = new EmailAddress("b@example.com", "B"), InheritedFromId = "7" }, "d");

        dto.GrantedToV2!.User.Email.ShouldBe("b@example.com");
        dto.InheritedFrom!.Id.ShouldBe("7");
        dto.Link.ShouldBeNull();
    }

    [Theory]
    [InlineData("small", ThumbnailSize.Small)]
    [InlineData("MEDIUM", ThumbnailSize.Medium)]
    [InlineData("large", ThumbnailSize.Large)]
    public void Thumbnail_sizes_parse_by_name(string name, ThumbnailSize size)
    {
        DriveUrls.TryParseSize(name, out var parsed).ShouldBeTrue();
        parsed.ShouldBe(size);
        DriveUrls.SizeName(size).ShouldBe(name.ToLowerInvariant());
    }

    [Fact]
    public void An_upload_url_allows_put_get_and_delete_only()
    {
        DriveUrls.Upload.Allows("PUT").ShouldBeTrue();
        DriveUrls.Upload.Allows("get").ShouldBeTrue();
        DriveUrls.Upload.Allows("DELETE").ShouldBeTrue();
        DriveUrls.Upload.Allows("POST").ShouldBeFalse();
        DriveUrls.Download.Allows("PUT").ShouldBeFalse();
        new SelfAuthorisingUrl("p", "GET", TimeSpan.FromMinutes(1), _ => null).Allows("GET").ShouldBeTrue();
    }

    private static string Encode(string path) => System.Buffers.Text.Base64Url.EncodeToString(System.Text.Encoding.UTF8.GetBytes(path));

    private static string Code(Error error) => ReadCode((Nightware.Api.Graph.GraphErrorResult)DriveRequests.Fail(error));

    private static string ReadCode(Nightware.Api.Graph.GraphErrorResult result)
    {
        var context = new DefaultHttpContext { RequestServices = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(new Microsoft.Extensions.DependencyInjection.ServiceCollection()) };
        context.Response.Body = new MemoryStream();
        result.ExecuteAsync(context).GetAwaiter().GetResult();
        context.Response.Body.Position = 0;
        return JsonNode.Parse(context.Response.Body)!["error"]!["code"]!.GetValue<string>();
    }
}

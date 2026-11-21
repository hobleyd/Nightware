using System.Net;
using Nightware.Api.Tests.Support;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Api.Tests.Features.Mail.Write;

public sealed class FolderWriteEndpointTests : MailWriteEndpointTestBase
{
    private static MailFolder Folder(string name, string parent = "msgfolderroot") =>
        new(new FolderId("mbx-1"), name, new FolderId(parent), 0, 1, 12);

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task Create_child_under_the_root_is_201_with_the_folder(string prefix, string address)
    {
        Folders.CreateAsync(Box(address), new FolderRef.WellKnown(WellKnownFolder.MsgFolderRoot), "Projects", Arg.Any<CancellationToken>()).Returns(Folder("Projects"));

        var response = await SendAsync(HttpMethod.Post, $"{prefix}/mailFolders/msgfolderroot/childFolders", """{"displayName":"Projects"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await response.ReadObjectAsync();
        body["id"]!.GetValue<string>().ShouldBe("mbx-1");
        body["displayName"]!.GetValue<string>().ShouldBe("Projects");
        body["parentFolderId"]!.GetValue<string>().ShouldBe("msgfolderroot");
        body["@odata.context"]!.GetValue<string>().ShouldEndWith($"{Context(address)}/mailFolders('msgfolderroot')/childFolders/$entity");
    }

    [Fact]
    public async Task Create_child_of_a_folder_id_passes_the_id()
    {
        Folders.CreateAsync(default, null, null!, Ct).ReturnsForAnyArgs(Folder("Sub", "mbx-0"));

        (await SendAsync(HttpMethod.Post, "/v1.0/me/mailFolders/mbx-0/childFolders", """{"displayName":"Sub"}""")).StatusCode.ShouldBe(HttpStatusCode.Created);

        await Folders.Received(1).CreateAsync(Box(TestHost.UserEmail), new FolderRef.ById(new FolderId("mbx-0")), "Sub", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_child_with_an_existing_name_is_409_ErrorFolderExists()
    {
        Folders.CreateAsync(default, null, null!, Ct)
            .ReturnsForAnyArgs(Result<MailFolder>.Failure(new Error(ErrorKind.Conflict, "ErrorFolderExists", "exists", "displayName")));

        var response = await SendAsync(HttpMethod.Post, "/v1.0/me/mailFolders/msgfolderroot/childFolders", """{"displayName":"Projects"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await response.ReadObjectAsync()).Str("error.code").ShouldBe("ErrorFolderExists");
    }

    [Fact]
    public async Task Create_child_without_a_name_is_400()
    {
        var response = await SendAsync(HttpMethod.Post, "/v1.0/me/mailFolders/msgfolderroot/childFolders", """{"displayName":"  "}""");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await Folders.DidNotReceiveWithAnyArgs().CreateAsync(default, null, null!, Ct);
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task Rename_is_200_with_the_folder(string prefix, string address)
    {
        Folders.RenameAsync(Box(address), new FolderId("mbx-1"), "Projects 2026", Arg.Any<CancellationToken>()).Returns(Folder("Projects 2026"));

        var response = await SendAsync(HttpMethod.Patch, $"{prefix}/mailFolders/mbx-1", """{"displayName":"Projects 2026"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadObjectAsync();
        body["displayName"]!.GetValue<string>().ShouldBe("Projects 2026");
        body["@odata.context"]!.GetValue<string>().ShouldEndWith($"{Context(address)}/mailFolders/$entity");
    }

    [Fact]
    public async Task Rename_of_a_well_known_folder_is_403()
    {
        Folders.RenameAsync(default, default, null!, Ct).ReturnsForAnyArgs(Result<MailFolder>.Failure(Error.Forbidden("no")));

        (await SendAsync(HttpMethod.Patch, "/v1.0/me/mailFolders/mbx-inbox", """{"displayName":"x"}""")).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task Delete_is_204(string prefix, string address)
    {
        Folders.DeleteAsync(Box(address), new FolderId("mbx-1"), Arg.Any<CancellationToken>()).Returns(Result.Success());

        (await SendAsync(HttpMethod.Delete, $"{prefix}/mailFolders/mbx-1")).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Delete_of_a_missing_folder_is_404()
    {
        Folders.DeleteAsync(default, default, Ct).ReturnsForAnyArgs(Result.Failure(Error.NotFound("no")));

        var response = await SendAsync(HttpMethod.Delete, "/v1.0/me/mailFolders/mbx-x");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.ReadObjectAsync()).Str("error.code").ShouldBe("ErrorItemNotFound");
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task Move_to_the_root_is_201_with_the_folder(string prefix, string address)
    {
        Folders.MoveAsync(Box(address), new FolderId("mbx-1"), new FolderRef.WellKnown(WellKnownFolder.MsgFolderRoot), Arg.Any<CancellationToken>()).Returns(Folder("Projects"));

        var response = await SendAsync(HttpMethod.Post, $"{prefix}/mailFolders/mbx-1/move", """{"destinationId":"msgfolderroot"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await response.ReadObjectAsync())["id"]!.GetValue<string>().ShouldBe("mbx-1");
    }

    [Fact]
    public async Task Move_without_a_destination_is_400()
    {
        var response = await SendAsync(HttpMethod.Post, "/v1.0/me/mailFolders/mbx-1/move", "{}");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadObjectAsync()).Str("error.code").ShouldBe("invalidRequest");
    }

    [Fact]
    public async Task Move_into_its_own_subtree_is_400()
    {
        Folders.MoveAsync(default, default, null!, Ct).ReturnsForAnyArgs(Result<MailFolder>.Failure(Error.Validation("cycle")));

        (await SendAsync(HttpMethod.Post, "/v1.0/me/mailFolders/mbx-1/move", """{"destinationId":"mbx-2"}""")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await Folders.Received(1).MoveAsync(Arg.Any<MailboxRef>(), new FolderId("mbx-1"), new FolderRef.ById(new FolderId("mbx-2")), Arg.Any<CancellationToken>());
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task Create_at_the_top_level_is_201_under_the_root(string prefix, string address)
    {
        Folders.CreateAsync(Box(address), new FolderRef.WellKnown(WellKnownFolder.MsgFolderRoot), "Plans", Arg.Any<CancellationToken>()).Returns(Folder("Plans"));

        var response = await SendAsync(HttpMethod.Post, $"{prefix}/mailFolders", """{"displayName":"Plans","isHidden":false}""");

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await response.ReadObjectAsync();
        body["displayName"]!.GetValue<string>().ShouldBe("Plans");
        body["@odata.context"]!.GetValue<string>().ShouldEndWith($"{Context(address)}/mailFolders/$entity");
    }

    [Fact]
    public async Task Create_a_hidden_folder_is_400_notSupported()
    {
        var response = await SendAsync(HttpMethod.Post, "/v1.0/me/mailFolders", """{"displayName":"Clutter","isHidden":true}""");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadObjectAsync()).Str("error.code").ShouldBe("notSupported");
        await Folders.DidNotReceiveWithAnyArgs().CreateAsync(default, null, null!, Ct);
    }

    [Fact]
    public async Task Create_at_the_top_level_without_a_name_is_400()
    {
        (await SendAsync(HttpMethod.Post, "/v1.0/me/mailFolders", "{}")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public async Task Copy_is_200_with_the_new_folder(string prefix, string address)
    {
        Folders.CopyAsync(Box(address), new FolderId("mbx-1"), new FolderRef.WellKnown(WellKnownFolder.Inbox), Arg.Any<CancellationToken>())
            .Returns(new MailFolder(new FolderId("mbx-9"), "Projects", new FolderId("mbx-inbox"), 0, 1, 12));

        var response = await SendAsync(HttpMethod.Post, $"{prefix}/mailFolders/mbx-1/copy", """{"destinationId":"inbox"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadObjectAsync();
        body["id"]!.GetValue<string>().ShouldBe("mbx-9");
        body["@odata.context"]!.GetValue<string>().ShouldEndWith($"{Context(address)}/mailFolders/$entity");
    }

    [Fact]
    public async Task Copy_without_a_destination_is_400()
    {
        (await SendAsync(HttpMethod.Post, "/v1.0/me/mailFolders/mbx-1/copy", "{}")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await Folders.DidNotReceiveWithAnyArgs().CopyAsync(default, default, null!, Ct);
    }

    [Fact]
    public async Task Copy_to_a_name_that_exists_is_409()
    {
        Folders.CopyAsync(default, default, null!, Ct)
            .ReturnsForAnyArgs(Result<MailFolder>.Failure(new Error(ErrorKind.Conflict, "ErrorFolderExists", "exists", "displayName")));

        var response = await SendAsync(HttpMethod.Post, "/v1.0/me/mailFolders/mbx-1/copy", """{"destinationId":"msgfolderroot"}""");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }
}

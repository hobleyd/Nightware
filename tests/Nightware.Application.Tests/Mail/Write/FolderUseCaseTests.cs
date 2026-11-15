using Nightware.Application.Mail;
using Nightware.Application.Mail.Write;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Tests.Mail.Write;

public sealed class FolderUseCaseTests
{
    private static readonly MailboxRef Me = new("me@example.com");
    private static readonly FolderId Id = new("f1");
    private static readonly FolderRef Root = new FolderRef.WellKnown(WellKnownFolder.MsgFolderRoot);
    private static readonly MailFolder Folder = new(Id, "Projects", new FolderId("msgfolderroot"), 0, 0, 0);

    private readonly IMailFolderWriter _folders = Substitute.For<IMailFolderWriter>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Create_child_trims_the_name()
    {
        _folders.CreateAsync(Me, Root, "Projects", Arg.Any<CancellationToken>()).Returns(Folder);

        (await new CreateChildFolder(_folders).ExecuteAsync(Me, Root, "  Projects ", Ct)).Value.ShouldBe(Folder);
    }

    [Fact]
    public async Task Create_child_returns_a_conflict_unchanged()
    {
        var exists = new Error(ErrorKind.Conflict, "ErrorFolderExists", "exists");
        _folders.CreateAsync(Me, Root, "Projects", Arg.Any<CancellationToken>()).Returns(exists);

        (await new CreateChildFolder(_folders).ExecuteAsync(Me, Root, "Projects", Ct)).Error.ShouldBe(exists);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task A_folder_needs_a_name(string? name)
    {
        var created = await new CreateChildFolder(_folders).ExecuteAsync(Me, Root, name, Ct);
        var renamed = await new RenameFolder(_folders).ExecuteAsync(Me, Id, name, Ct);

        foreach (var error in new[] { created.Error, renamed.Error })
        {
            error!.Kind.ShouldBe(ErrorKind.Validation);
            error.Target.ShouldBe("displayName");
        }

        await _folders.DidNotReceiveWithAnyArgs().CreateAsync(default, default, default!, Ct);
        await _folders.DidNotReceiveWithAnyArgs().RenameAsync(default, default, default!, Ct);
    }

    [Fact]
    public async Task Rename_trims_the_name()
    {
        _folders.RenameAsync(Me, Id, "Projects 2026", Arg.Any<CancellationToken>()).Returns(Folder);

        (await new RenameFolder(_folders).ExecuteAsync(Me, Id, " Projects 2026\t", Ct)).Value.ShouldBe(Folder);
    }

    [Fact]
    public async Task Move_and_delete_pass_through()
    {
        _folders.MoveAsync(Me, Id, Root, Arg.Any<CancellationToken>()).Returns(Folder);
        _folders.DeleteAsync(Me, Id, Arg.Any<CancellationToken>()).Returns(Result.Success());

        (await new MoveFolder(_folders).ExecuteAsync(Me, Id, Root, Ct)).Value.ShouldBe(Folder);
        (await new DeleteFolder(_folders).ExecuteAsync(Me, Id, Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Folder_use_cases_reject_null_folder_refs()
    {
        await Should.ThrowAsync<ArgumentNullException>(() => new CreateChildFolder(_folders).ExecuteAsync(Me, null!, "x", Ct));
        await Should.ThrowAsync<ArgumentNullException>(() => new MoveFolder(_folders).ExecuteAsync(Me, Id, null!, Ct));
    }
}

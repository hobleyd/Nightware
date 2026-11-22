using Nightware.Application.Files.Drives;
using Nightware.Domain.Files;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Files;

public sealed class DriveItemUseCaseTests
{
    private static readonly DriveItemRef Item = DriveItemRef.ById("42");
    private static readonly DriveItem File = new("42", "notes.txt") { Size = 3 };
    private static readonly DriveItem Folder = new("7", "Reports") { IsFolder = true };
    private static readonly DriveListQuery List = new(10, null);

    private readonly IDriveStore _store = Substitute.For<IDriveStore>();
    private readonly IDriveDelta _delta = Substitute.For<IDriveDelta>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Get_drive_and_get_item_pass_the_store_through()
    {
        var drive = new Drive("nc!YWxpY2U", "OneDrive");
        _store.GetDriveAsync("nc!YWxpY2U", Ct).Returns(drive);
        _store.GetItemAsync(Item, Ct).Returns(File);

        (await new GetDrive(_store).ExecuteAsync("nc!YWxpY2U", Ct)).Value.ShouldBeSameAs(drive);
        (await new GetDriveItem(_store).ExecuteAsync(Item, Ct)).Value.ShouldBeSameAs(File);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1001)]
    public async Task Children_refuse_a_page_size_out_of_range(int top)
    {
        var result = await new ListDriveItemChildren(_store).ExecuteAsync(Item, new DriveListQuery(top, null), Ct);

        result.Error!.Target.ShouldBe("$top");
        await _store.DidNotReceiveWithAnyArgs().ListChildrenAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task Children_come_from_the_store()
    {
        var page = new Page<DriveItem>([File]);
        _store.ListChildrenAsync(Item, List, Ct).Returns(page);

        (await new ListDriveItemChildren(_store).ExecuteAsync(Item, List, Ct)).Value.ShouldBeSameAs(page);
    }

    [Fact]
    public async Task Prepare_download_allows_a_file_and_refuses_a_folder()
    {
        _store.GetItemAsync(Item, Ct).Returns(File);
        (await new PrepareDriveItemDownload(_store).ExecuteAsync(Item, Ct)).Value.ShouldBeSameAs(File);

        _store.GetItemAsync(Item, Ct).Returns(Folder);
        (await new PrepareDriveItemDownload(_store).ExecuteAsync(Item, Ct)).Error!.Code.ShouldBe(DriveErrors.NotSupportedCode);
    }

    [Fact]
    public async Task Prepare_download_passes_a_missing_item_through()
    {
        _store.GetItemAsync(Item, Ct).Returns(Error.NotFound("gone"));

        (await new PrepareDriveItemDownload(_store).ExecuteAsync(Item, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Download_reads_the_store()
    {
        var blob = new BlobContent("text/plain", 3, _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])));
        _store.OpenContentAsync(Item, Ct).Returns(blob);

        (await new DownloadDriveItem(_store).ExecuteAsync(Item, Ct)).Value.ShouldBeSameAs(blob);
    }

    [Fact]
    public async Task Upload_refuses_the_root_and_a_body_over_the_simple_limit()
    {
        var useCase = new UploadDriveItemContent(_store);
        var small = new DriveUpload(Stream.Null, "text/plain", 3, ConflictBehavior.Replace);

        (await useCase.ExecuteAsync(DriveItemRef.Root, small, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await useCase.ExecuteAsync(Item, small with { Length = DriveLimits.SimpleUploadMaxBytes + 1 }, Ct)).Error!.Kind.ShouldBe(ErrorKind.PayloadTooLarge);
        await _store.DidNotReceiveWithAnyArgs().WriteContentAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task Upload_writes_through_the_store()
    {
        var upload = new DriveUpload(Stream.Null, "text/plain", 3, ConflictBehavior.Fail);
        _store.WriteContentAsync(Item, upload, Ct).Returns(File);

        (await new UploadDriveItemContent(_store).ExecuteAsync(Item, upload, Ct)).Value.ShouldBeSameAs(File);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public async Task A_folder_needs_a_valid_name(string? name)
    {
        (await new CreateDriveFolder(_store).ExecuteAsync(DriveItemRef.Root, name, ConflictBehavior.Fail, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public async Task A_folder_is_created_by_the_store()
    {
        _store.CreateFolderAsync(DriveItemRef.Root, "Reports", ConflictBehavior.Rename, Ct).Returns(Folder);

        (await new CreateDriveFolder(_store).ExecuteAsync(DriveItemRef.Root, "Reports", ConflictBehavior.Rename, Ct)).Value.ShouldBeSameAs(Folder);
    }

    [Fact]
    public async Task Update_checks_the_name_and_refuses_to_move_the_root()
    {
        var useCase = new UpdateDriveItem(_store);

        (await useCase.ExecuteAsync(Item, new DriveItemChange("a/b", null, ConflictBehavior.Fail), Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await useCase.ExecuteAsync(DriveItemRef.Root, new DriveItemChange("x", null, ConflictBehavior.Fail), Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public async Task Update_with_nothing_to_change_answers_the_item()
    {
        _store.GetItemAsync(Item, Ct).Returns(File);

        (await new UpdateDriveItem(_store).ExecuteAsync(Item, new DriveItemChange(null, null, ConflictBehavior.Fail), Ct)).Value.ShouldBeSameAs(File);
        await _store.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task Update_renames_through_the_store()
    {
        var change = new DriveItemChange("b.txt", DriveItemRef.ById("7"), ConflictBehavior.Replace);
        _store.UpdateAsync(Item, change, Ct).Returns(File);

        (await new UpdateDriveItem(_store).ExecuteAsync(Item, change, Ct)).Value.ShouldBeSameAs(File);
    }

    [Fact]
    public async Task Delete_refuses_the_root_and_deletes_anything_else()
    {
        (await new DeleteDriveItem(_store).ExecuteAsync(DriveItemRef.Root, null, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);

        _store.DeleteAsync(Item, "\"e\"", Ct).Returns(Result.Success());
        (await new DeleteDriveItem(_store).ExecuteAsync(Item, "\"e\"", Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Copy_checks_the_name_and_refuses_the_root()
    {
        var useCase = new CopyDriveItem(_store);

        (await useCase.ExecuteAsync(Item, null, "..", ConflictBehavior.Fail, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await useCase.ExecuteAsync(DriveItemRef.Root, null, null, ConflictBehavior.Fail, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);

        _store.CopyAsync(Item, Folder.Id is { } ? DriveItemRef.ById("7") : null, "copy.txt", ConflictBehavior.Rename, Ct).Returns(File);
        (await useCase.ExecuteAsync(Item, DriveItemRef.ById("7"), "copy.txt", ConflictBehavior.Rename, Ct)).Value.ShouldBeSameAs(File);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("a\u0001b")]
    public async Task Search_needs_text(string? text)
    {
        (await new SearchDrive(_store).ExecuteAsync(text, List, Ct)).Error!.Target.ShouldBe("q");
    }

    [Fact]
    public async Task Search_trims_its_text_and_checks_the_page_size()
    {
        var page = new Page<DriveItem>([File]);
        _store.SearchAsync("notes", List, Ct).Returns(page);

        (await new SearchDrive(_store).ExecuteAsync("  notes ", List, Ct)).Value.ShouldBeSameAs(page);
        (await new SearchDrive(_store).ExecuteAsync("notes", new DriveListQuery(0, null), Ct)).Error!.Target.ShouldBe("$top");
    }

    [Theory]
    [InlineData("documents", SpecialFolder.Documents)]
    [InlineData("Photos", SpecialFolder.Photos)]
    [InlineData("cameraroll", SpecialFolder.CameraRoll)]
    [InlineData("music", SpecialFolder.Music)]
    [InlineData("approot", SpecialFolder.AppRoot)]
    public async Task Special_folders_are_known_by_graph_name(string name, SpecialFolder folder)
    {
        _store.GetSpecialFolderAsync(folder, "nightmail", Ct).Returns(Folder);

        (await new GetSpecialFolder(_store).ExecuteAsync(name, "nightmail", Ct)).Value.ShouldBeSameAs(Folder);
    }

    [Fact]
    public async Task An_unknown_special_folder_is_not_found_and_the_app_folder_needs_an_app()
    {
        (await new GetSpecialFolder(_store).ExecuteAsync("recordings", "app", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await new GetSpecialFolder(_store).ExecuteAsync("approot", null, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public async Task Delta_defaults_its_page_size_and_refuses_one_out_of_range()
    {
        var page = DeltaPage<DriveItem>.Done([], new DeltaToken("t"));
        _delta.ChangesAsync(null, DriveLimits.DefaultPageSize, false, Ct).Returns(page);

        (await new GetDriveDelta(_delta).ExecuteAsync(null, null, false, Ct)).Value.ShouldBeSameAs(page);
        (await new GetDriveDelta(_delta).ExecuteAsync(null, 5000, false, Ct)).Error!.Target.ShouldBe("$top");
    }

    [Fact]
    public async Task A_thumbnail_comes_from_the_store()
    {
        var blob = new BlobContent("image/png", 3, _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])));
        _store.OpenThumbnailAsync(Item, ThumbnailSize.Small, Ct).Returns(blob);

        (await new OpenDriveThumbnail(_store).ExecuteAsync(Item, ThumbnailSize.Small, Ct)).Value.ShouldBeSameAs(blob);
    }
}

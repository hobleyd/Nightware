using Nightware.Application.Files.Drives;
using Nightware.Application.Mail.Write;
using Nightware.Domain.Files;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Files;

public sealed class DriveUploadAndSharingUseCaseTests
{
    private static readonly DriveItemRef Item = DriveItemRef.ById("42");

    private readonly IDriveUploads _uploads = Substitute.For<IDriveUploads>();
    private readonly IDriveSharing _sharing = Substitute.For<IDriveSharing>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_session_needs_a_valid_name_and_a_size_within_the_limit()
    {
        var useCase = new BeginDriveUpload(_uploads);

        (await useCase.ExecuteAsync(new DriveUploadFile(DriveItemRef.Root, "a/b", 1, ConflictBehavior.Fail), Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await useCase.ExecuteAsync(new DriveUploadFile(DriveItemRef.Root, "a", -1, ConflictBehavior.Fail), Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await useCase.ExecuteAsync(new DriveUploadFile(DriveItemRef.Root, "a", DriveLimits.UploadSessionMaxBytes + 1, ConflictBehavior.Fail), Ct))
            .Error!.Kind.ShouldBe(ErrorKind.PayloadTooLarge);
    }

    [Fact]
    public async Task A_session_of_unknown_size_is_opened_by_the_store()
    {
        var file = new DriveUploadFile(DriveItemRef.Root, "big.bin", 0, ConflictBehavior.Replace);
        var state = new DriveUploadState("s", DateTimeOffset.UnixEpoch, 0);
        _uploads.BeginAsync(file, Ct).Returns(state);

        (await new BeginDriveUpload(_uploads).ExecuteAsync(file, Ct)).Value.ShouldBeSameAs(state);
    }

    [Theory]
    [InlineData(10, 5, 100)]
    [InlineData(-1, 5, 100)]
    [InlineData(0, 100, 100)]
    public async Task A_chunk_must_be_a_range_of_the_file(long first, long last, long total)
    {
        (await new UploadDriveChunk(_uploads).ExecuteAsync("s", new ContentRangeHeader(first, last, total), Stream.Null, Ct)).Error!.Target.ShouldBe("Content-Range");
    }

    [Fact]
    public async Task A_chunk_over_sixty_mebibytes_or_a_file_over_the_limit_is_refused()
    {
        var useCase = new UploadDriveChunk(_uploads);

        (await useCase.ExecuteAsync("s", new ContentRangeHeader(0, DriveLimits.MaxChunkBytes, DriveLimits.MaxChunkBytes * 2), Stream.Null, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await useCase.ExecuteAsync("s", new ContentRangeHeader(0, 9, DriveLimits.UploadSessionMaxBytes + 1), Stream.Null, Ct)).Error!.Kind.ShouldBe(ErrorKind.PayloadTooLarge);
    }

    [Fact]
    public async Task A_chunk_is_appended_with_its_length()
    {
        var progress = new DriveUploadProgress(10, null);
        _uploads.AppendAsync("s", 0, 10, 20, Stream.Null, Ct).Returns(progress);

        (await new UploadDriveChunk(_uploads).ExecuteAsync("s", new ContentRangeHeader(0, 9, 20), Stream.Null, Ct)).Value.ShouldBeSameAs(progress);
    }

    [Fact]
    public async Task Status_and_cancel_pass_the_store_through()
    {
        var state = new DriveUploadState("s", DateTimeOffset.UnixEpoch, 5);
        _uploads.GetAsync("s", Ct).Returns(state);
        _uploads.CancelAsync("s", Ct).Returns(Result.Success());

        (await new GetDriveUpload(_uploads).ExecuteAsync("s", Ct)).Value.ShouldBeSameAs(state);
        (await new CancelDriveUpload(_uploads).ExecuteAsync("s", Ct)).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null, null, ErrorKind.Validation, "invalidRequest")]
    [InlineData("embed", null, ErrorKind.Validation, DriveErrors.NotSupportedCode)]
    [InlineData("view", "organization", ErrorKind.Validation, DriveErrors.NotSupportedCode)]
    public async Task A_link_is_view_or_edit_and_anonymous(string? type, string? scope, ErrorKind kind, string code)
    {
        var error = (await new CreateSharingLink(_sharing).ExecuteAsync(Item, type, scope, null, null, Ct)).Error!;

        error.Kind.ShouldBe(kind);
        error.Code.ShouldBe(code);
    }

    [Fact]
    public async Task The_root_is_never_shared()
    {
        (await new CreateSharingLink(_sharing).ExecuteAsync(DriveItemRef.Root, "view", null, null, null, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        (await new InviteToDriveItem(_sharing).ExecuteAsync(DriveItemRef.Root, new DriveInvitation(["bob@example.com"], ["read"], null), Ct))
            .Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task A_link_is_created_with_its_type_lower_cased_and_no_empty_password()
    {
        var share = new DriveShare("33", ["write"]);
        _sharing.CreateLinkAsync(Item, new SharingLinkRequest("edit", null, null), Ct).Returns(share);

        (await new CreateSharingLink(_sharing).ExecuteAsync(Item, "Edit", "anonymous", null, string.Empty, Ct)).Value.ShouldBeSameAs(share);
    }

    [Fact]
    public async Task An_invitation_needs_recipients_and_read_or_write()
    {
        var useCase = new InviteToDriveItem(_sharing);

        (await useCase.ExecuteAsync(Item, new DriveInvitation([], ["read"], null), Ct)).Error!.Target.ShouldBe("recipients");
        (await useCase.ExecuteAsync(Item, new DriveInvitation([" "], ["read"], null), Ct)).Error!.Target.ShouldBe("recipients");
        (await useCase.ExecuteAsync(Item, new DriveInvitation(["bob@example.com"], ["owner"], null), Ct)).Error!.Code.ShouldBe(DriveErrors.NotSupportedCode);
        (await useCase.ExecuteAsync(Item, new DriveInvitation(["bob@example.com"], [], null), Ct)).Error!.Code.ShouldBe(DriveErrors.NotSupportedCode);
    }

    [Fact]
    public async Task Invitations_permissions_and_removal_reach_the_store()
    {
        var invitation = new DriveInvitation(["bob@example.com"], ["write"], null);
        IReadOnlyList<DriveShare> shares = [new DriveShare("34", ["write"])];
        _sharing.InviteAsync(Item, invitation, Ct).Returns(Result<IReadOnlyList<DriveShare>>.Success(shares));
        _sharing.ListPermissionsAsync(Item, Ct).Returns(Result<IReadOnlyList<DriveShare>>.Success(shares));
        _sharing.DeletePermissionAsync(Item, "34", Ct).Returns(Result.Success());

        (await new InviteToDriveItem(_sharing).ExecuteAsync(Item, invitation, Ct)).Value.ShouldBeSameAs(shares);
        (await new ListDriveItemPermissions(_sharing).ExecuteAsync(Item, Ct)).Value.ShouldBeSameAs(shares);
        (await new RemoveDriveItemShare(_sharing).ExecuteAsync(Item, "34", Ct)).IsSuccess.ShouldBeTrue();
    }
}

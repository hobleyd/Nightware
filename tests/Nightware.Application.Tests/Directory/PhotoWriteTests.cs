using Nightware.Application.Common.Identity;
using Nightware.Application.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Directory;

/// <summary>Setting, removing and reading one's own photo (phase 19).</summary>
public sealed class PhotoWriteTests
{
    private static readonly MailboxRef Me = new("me@example.com");

    private readonly IMailboxResolver _resolver = Substitute.For<IMailboxResolver>();
    private readonly IUserPhotoWriter _writer = Substitute.For<IUserPhotoWriter>();
    private readonly IUserPhotoReader _reader = Substitute.For<IUserPhotoReader>();

    public PhotoWriteTests()
    {
        _resolver.ResolveAsync(null, Arg.Any<CancellationToken>()).Returns(Me);
        _resolver.ResolveAsync("Me@Example.com", Arg.Any<CancellationToken>()).Returns(new MailboxRef("me@example.com"));
        _resolver.ResolveAsync("id-me", Arg.Any<CancellationToken>()).Returns(new MailboxRef("ME@example.com"));
        _resolver.ResolveAsync("sam@example.com", Arg.Any<CancellationToken>()).Returns(new MailboxRef("sam@example.com"));
        _resolver.ResolveAsync("nobody", Arg.Any<CancellationToken>()).Returns(Error.Validation("unknown"));
        _writer.SetAsync(default, default, null!, default).ReturnsForAnyArgs(Result.Success());
        _writer.DeleteAsync(default, default).ReturnsForAnyArgs(Result.Success());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SetUserPhoto Set => new(_resolver, _writer);

    [Theory]
    [InlineData(null)]
    [InlineData("Me@Example.com")]
    [InlineData("id-me")]
    public async Task The_caller_sets_their_own_square_photo_typed_by_its_bytes(string? user)
    {
        (await Set.ExecuteAsync(user, TestImages.Png(16, 16), Ct)).IsSuccess.ShouldBeTrue();

        await _writer.Received(1).SetAsync(Me, Arg.Any<ReadOnlyMemory<byte>>(), ImageInfo.Png, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_jpeg_goes_as_a_jpeg()
    {
        await Set.ExecuteAsync(null, TestImages.Jpeg8, Ct);

        await _writer.Received(1).SetAsync(Me, Arg.Any<ReadOnlyMemory<byte>>(), ImageInfo.Jpeg, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Someone_elses_photo_is_403_and_an_unknown_user_404()
    {
        (await Set.ExecuteAsync("sam@example.com", TestImages.Jpeg8, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        (await Set.ExecuteAsync("nobody", TestImages.Jpeg8, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await new DeleteUserPhoto(_resolver, _writer).ExecuteAsync("sam@example.com", Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        await _writer.DidNotReceiveWithAnyArgs().SetAsync(default, default, null!, Ct);
        await _writer.DidNotReceiveWithAnyArgs().DeleteAsync(default, Ct);
    }

    [Fact]
    public async Task A_non_square_image_is_400_naming_its_size()
    {
        var error = (await Set.ExecuteAsync(null, TestImages.Png(40, 20), Ct)).Error!;

        error.Kind.ShouldBe(ErrorKind.Validation);
        error.Message.ShouldContain("40x20");
    }

    [Fact]
    public async Task Not_an_image_is_400_and_over_4MB_is_413()
    {
        (await Set.ExecuteAsync(null, "GIF89a"u8.ToArray(), Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await Set.ExecuteAsync(null, new byte[UserPhotos.MaxUploadBytes + 1], Ct)).Error!.Kind.ShouldBe(ErrorKind.PayloadTooLarge);
        await _writer.DidNotReceiveWithAnyArgs().SetAsync(default, default, null!, Ct);
    }

    [Fact]
    public async Task The_caller_removes_their_own_photo()
    {
        (await new DeleteUserPhoto(_resolver, _writer).ExecuteAsync(null, Ct)).IsSuccess.ShouldBeTrue();

        await _writer.Received(1).DeleteAsync(Me, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unresolvable_caller_is_returned()
    {
        _resolver.ResolveAsync(null, Arg.Any<CancellationToken>()).Returns(Error.Forbidden("no email claim"));

        (await new DeleteUserPhoto(_resolver, _writer).ExecuteAsync(null, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        (await new GetSignedInUserPhoto(_resolver, _reader).ExecuteAsync(Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task The_signed_in_users_photo_is_read_by_their_address_lower_cased()
    {
        _resolver.ResolveAsync(null, Arg.Any<CancellationToken>()).Returns(new MailboxRef("Me@Example.com"));
        var png = new BlobContent("image/png", 3, static _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])));
        _reader.GetAsync("me@example.com", Arg.Any<CancellationToken>()).Returns(png);

        (await new GetSignedInUserPhoto(_resolver, _reader).ExecuteAsync(Ct)).Value.ShouldBe(png);
    }
}

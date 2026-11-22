using Nightware.Application.Common.Identity;
using Nightware.Application.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Directory;

public sealed class GetUserPhotoInfoTests
{
    private readonly IMailboxResolver _resolver = Substitute.For<IMailboxResolver>();
    private readonly IUserDirectory _directory = Substitute.For<IUserDirectory>();
    private readonly IUserPhotoReader _photos = Substitute.For<IUserPhotoReader>();

    public GetUserPhotoInfoTests() => _resolver.ResolveAsync(null, Arg.Any<CancellationToken>()).Returns(new MailboxRef("me@example.com"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private GetUserPhotoInfo UseCase => new(new GetSignedInUserPhoto(_resolver, _photos), new GetUserPhoto(_directory, _photos));

    private static BlobContent Blob(byte[] bytes, string type = "image/png") => new(type, bytes.Length, _ => Task.FromResult<Stream>(new MemoryStream(bytes)));

    [Fact]
    public async Task The_size_type_and_a_content_tag_come_from_the_image()
    {
        var png = TestImages.Png(64, 64);
        _photos.GetAsync("me@example.com", Arg.Any<CancellationToken>()).Returns(Blob(png));

        var info = (await UseCase.ExecuteAsync(null, Ct)).Value;

        info.ContentType.ShouldBe("image/png");
        (info.Width, info.Height).ShouldBe((64, 64));
        info.Etag.ShouldMatch("^\"[0-9A-F]{8}\"$");
        (await UseCase.ExecuteAsync(null, Ct)).Value.Etag.ShouldBe(info.Etag);
    }

    [Fact]
    public async Task Another_users_photo_is_read_through_the_directory_lookup()
    {
        _photos.GetAsync("sam@example.com", Arg.Any<CancellationToken>()).Returns(Blob(TestImages.Jpeg8, "image/jpeg"));

        (await UseCase.ExecuteAsync("Sam@example.com", Ct)).Value.ContentType.ShouldBe("image/jpeg");
    }

    [Fact]
    public async Task No_photo_is_Graphs_1x1_placeholder_but_an_unknown_user_is_404()
    {
        _photos.GetAsync("me@example.com", Arg.Any<CancellationToken>()).Returns(UserPhotos.None());
        _photos.GetAsync("x@example.com", Arg.Any<CancellationToken>()).Returns(Error.NotFound("no such user"));

        (await UseCase.ExecuteAsync(null, Ct)).Value.ShouldBe(UserPhotoInfo.None);
        (await UseCase.ExecuteAsync("x@example.com", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task An_image_whose_header_cant_be_read_keeps_its_type_with_no_size()
    {
        _photos.GetAsync("me@example.com", Arg.Any<CancellationToken>()).Returns(Blob([1, 2, 3], "image/webp"));

        var info = (await UseCase.ExecuteAsync(null, Ct)).Value;

        (info.ContentType, info.Width, info.Height).ShouldBe(("image/webp", 0, 0));
    }
}

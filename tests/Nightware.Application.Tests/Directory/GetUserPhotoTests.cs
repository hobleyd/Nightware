using Nightware.Application.Directory;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Directory;

public sealed class GetUserPhotoTests
{
    private readonly IUserDirectory _directory = Substitute.For<IUserDirectory>();
    private readonly IUserPhotoReader _photos = Substitute.For<IUserPhotoReader>();
    private readonly BlobContent _png = new("image/png", 3, static _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3])));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private GetUserPhoto UseCase => new(_directory, _photos);

    [Fact]
    public async Task An_address_goes_straight_to_the_photo_store_lower_cased()
    {
        _photos.GetAsync("sam@example.com", Arg.Any<CancellationToken>()).Returns(_png);

        (await UseCase.ExecuteAsync("Sam@Example.com", Ct)).Value.ShouldBe(_png);
        await _directory.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_directory_id_is_resolved_to_the_users_address()
    {
        _directory.GetAsync("id-sam", Arg.Any<CancellationToken>()).Returns(new DirectoryUser("id-sam", "Sam", "sam") { Mail = "Sam@example.com" });
        _photos.GetAsync("sam@example.com", Arg.Any<CancellationToken>()).Returns(_png);

        (await UseCase.ExecuteAsync("id-sam", Ct)).Value.ShouldBe(_png);
    }

    [Fact]
    public async Task A_directory_id_falls_back_to_an_address_shaped_upn()
    {
        _directory.GetAsync("id-sam", Arg.Any<CancellationToken>()).Returns(new DirectoryUser("id-sam", "Sam", "sam@example.com"));
        _photos.GetAsync("sam@example.com", Arg.Any<CancellationToken>()).Returns(_png);

        (await UseCase.ExecuteAsync("id-sam", Ct)).Value.ShouldBe(_png);
    }

    [Fact]
    public async Task A_user_without_a_mailbox_has_no_photo()
    {
        _directory.GetAsync("id-svc", Arg.Any<CancellationToken>()).Returns(new DirectoryUser("id-svc", "Service", "svc"));

        (await UseCase.ExecuteAsync("id-svc", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        await _photos.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(ErrorKind.NotFound, ErrorKind.NotFound)]
    [InlineData(ErrorKind.Validation, ErrorKind.NotFound)]
    [InlineData(ErrorKind.BackendUnavailable, ErrorKind.BackendUnavailable)]
    public async Task Directory_misses_are_not_found(ErrorKind backend, ErrorKind expected)
    {
        _directory.GetAsync("id-x", Arg.Any<CancellationToken>()).Returns(new Error(backend, "c", "m"));

        (await UseCase.ExecuteAsync("id-x", Ct)).Error!.Kind.ShouldBe(expected);
    }

    [Fact]
    public async Task No_photo_is_not_found()
    {
        _photos.GetAsync("sam@example.com", Arg.Any<CancellationToken>()).Returns(Error.NotFound("no photo"));

        (await UseCase.ExecuteAsync("sam@example.com", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task A_blank_name_is_not_found()
    {
        (await UseCase.ExecuteAsync(" ", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }
}

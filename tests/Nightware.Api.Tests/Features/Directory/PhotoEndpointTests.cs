using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;

using Microsoft.Extensions.DependencyInjection;

using Nightware.Api.Tests.Support;
using Nightware.Application.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Tests.Features.Directory;

/// <summary>Phase 19 photo routes: the signed-in user's photo, the metadata, and setting or removing one's own.</summary>
public sealed class PhotoEndpointTests : IAsyncLifetime
{
    private readonly IUserDirectory _directory = Substitute.For<IUserDirectory>();
    private readonly IUserPhotoReader _photos = Substitute.For<IUserPhotoReader>();
    private readonly IUserPhotoWriter _writer = Substitute.For<IUserPhotoWriter>();
    private TestHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Me => TestHost.UserEmail;

    public async ValueTask InitializeAsync()
    {
        _writer.SetAsync(default, default, null!, default).ReturnsForAnyArgs(Result.Success());
        _writer.DeleteAsync(default, default).ReturnsForAnyArgs(Result.Success());
        _host = await TestHost.StartAsync(services => services.AddSingleton(_directory).AddSingleton(_photos).AddSingleton(_writer));
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    /// <summary>The first bytes of a PNG of the given size: all the header reader needs.</summary>
    private static byte[] PngHeader(int width, int height)
    {
        var bytes = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), height);
        return bytes;
    }

    private static BlobContent Blob(byte[] bytes) => new("image/png", bytes.Length, _ => Task.FromResult<Stream>(new MemoryStream(bytes)));

    private Task<HttpResponseMessage> PutAsync(string path, byte[] body, HttpMethod? method = null)
    {
        var content = new ByteArrayContent(body);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        return _host.AuthorizedClient().SendAsync(new HttpRequestMessage(method ?? HttpMethod.Put, path) { Content = content }, Ct);
    }

    [Fact]
    public async Task My_photo_value_is_streamed_and_404_ImageNotFound_without_one()
    {
        _photos.GetAsync(Me, Arg.Any<CancellationToken>()).Returns(Blob(PngHeader(48, 48)));

        var response = await _host.AuthorizedClient().GetAsync("/v1.0/me/photo/$value", Ct);

        response.Content.Headers.ContentType!.MediaType.ShouldBe("image/png");
        (await response.Content.ReadAsByteArrayAsync(Ct)).Length.ShouldBe(33);

        _photos.GetAsync(Me, Arg.Any<CancellationToken>()).Returns(UserPhotos.None());
        var none = await _host.AuthorizedClient().GetAsync("/v1.0/me/photo/$value", Ct);
        none.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await none.ReadObjectAsync()).Str("error.code").ShouldBe("ImageNotFound");
    }

    [Theory]
    [InlineData("/v1.0/me/photo", "me/photo")]
    [InlineData("/v1.0/users/sam@example.com/photo", "users('sam%40example.com')/photo")]
    public async Task Metadata_reads_the_size_from_the_image(string path, string context)
    {
        _photos.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Blob(PngHeader(240, 240)));

        var response = await _host.AuthorizedClient().GetAsync(path, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadObjectAsync();
        body["@odata.context"]!.GetValue<string>().ShouldEndWith($"$metadata#{context}/$entity");
        body["@odata.mediaContentType"]!.GetValue<string>().ShouldBe("image/png");
        body["@odata.mediaEtag"]!.GetValue<string>().ShouldStartWith("\"");
        body["id"]!.GetValue<string>().ShouldBe("240x240");
        body["width"]!.GetValue<int>().ShouldBe(240);
        body["height"]!.GetValue<int>().ShouldBe(240);
    }

    [Fact]
    public async Task Metadata_without_a_photo_is_the_1x1_placeholder()
    {
        _photos.GetAsync(Me, Arg.Any<CancellationToken>()).Returns(UserPhotos.None());

        var body = await (await _host.AuthorizedClient().GetAsync("/v1.0/me/photo", Ct)).ReadObjectAsync();

        body["@odata.mediaContentType"]!.GetValue<string>().ShouldBe("image/gif");
        body["@odata.mediaEtag"]!.GetValue<string>().ShouldBe(string.Empty);
        body["id"]!.GetValue<string>().ShouldBe("1x1");
    }

    [Theory]
    [InlineData("PUT", "/v1.0/me/photo/$value")]
    [InlineData("PATCH", "/v1.0/me/photo/$value")]
    [InlineData("PUT", "/v1.0/users/{me}/photo/$value")]
    public async Task Setting_my_own_square_photo_is_200_with_no_body(string method, string path)
    {
        var response = await PutAsync(path.Replace("{me}", Me, StringComparison.Ordinal), PngHeader(96, 96), new HttpMethod(method));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldBeEmpty();
        await _writer.Received(1).SetAsync(new MailboxRef(Me), Arg.Is<ReadOnlyMemory<byte>>(m => m.Length == 33), "image/png", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_non_square_photo_is_400_and_someone_elses_403()
    {
        (await PutAsync("/v1.0/me/photo/$value", PngHeader(40, 20))).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await PutAsync("/v1.0/users/sam@example.com/photo/$value", PngHeader(8, 8))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await _writer.DidNotReceiveWithAnyArgs().SetAsync(default, default, null!, Ct);
    }

    [Fact]
    public async Task A_photo_over_4MB_is_413()
    {
        var huge = new byte[UserPhotos.MaxUploadBytes + 10];
        PngHeader(8, 8).CopyTo(huge, 0);

        (await PutAsync("/v1.0/me/photo/$value", huge)).StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Deleting_my_photo_is_200_and_someone_elses_403()
    {
        (await _host.AuthorizedClient().DeleteAsync("/v1.0/me/photo/$value", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _host.AuthorizedClient().DeleteAsync("/v1.0/users/sam@example.com/photo/$value", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await _writer.Received(1).DeleteAsync(new MailboxRef(Me), Arg.Any<CancellationToken>());
    }
}

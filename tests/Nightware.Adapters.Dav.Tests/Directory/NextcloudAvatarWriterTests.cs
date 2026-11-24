using System.Net;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Nightware.Adapters.Dav.Core.Discovery;
using Nightware.Adapters.Dav.Directory;
using Nightware.Application.Common.Identity;
using Nightware.Application.Directory;
using Nightware.Domain.Kernel;

using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Nightware.Adapters.Dav.Tests.Directory;

/// <summary>The photo writer through the adapter's real registration, against a WireMock Nextcloud (bodies as Nextcloud 35 answers).</summary>
public sealed class NextcloudAvatarWriterTests : IDisposable
{
    private const string AvatarPath = "/nc/index.php/avatar/";
    private static readonly MailboxRef Bob = new("bob@nightware.localhost");
    /// <summary>A real 16 × 16 PNG: square, so it goes to Nextcloud untouched.</summary>
    private static readonly byte[] Png = SquarePng();

    private static byte[] SquarePng()
    {
        using var bitmap = new SkiaSharp.SKBitmap(16, 16);
        bitmap.Erase(SkiaSharp.SKColors.Teal);
        using var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly IBackendCredentials _credentials = Substitute.For<IBackendCredentials>();
    private readonly ServiceProvider _provider;

    public NextcloudAvatarWriterTests()
    {
        _credentials.ForAsync(Backend.Groupware, Arg.Any<CancellationToken>())
            .Returns(new BackendCredential("Bearer", "nc-token", CredentialStrategy.TokenExchange));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Dav:BaseUrl"] = _server.Url + "/nc/",
            ["Dav:MaxRetryAttempts"] = "0",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => _credentials);
        services.AddScoped(_ => Substitute.For<IDavPrincipalDiscovery>());
        services.AddDavAdapter(configuration);
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public void Dispose()
    {
        _provider.Dispose();
        _server.Stop();
        _server.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IUserPhotoWriter Writer => _provider.CreateScope().ServiceProvider.GetRequiredService<IUserPhotoWriter>();

    private void Given(string method, int status, string body) =>
        _server.Given(Request.Create().WithPath(AvatarPath).UsingMethod(method))
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body));

    [Fact]
    public async Task Set_posts_the_image_as_files_with_the_bearer_and_the_OCS_header()
    {
        Given("POST", 200, """{"status":"success"}""");

        (await Writer.SetAsync(Bob, Png, Ct)).IsSuccess.ShouldBeTrue();

        var request = _server.LogEntries.ShouldHaveSingleItem().RequestMessage!;
        request.Headers!["Authorization"].ShouldBe(["Bearer nc-token"]);
        request.Headers["OCS-APIRequest"].ShouldBe(["true"]);
        request.Headers["Content-Type"].Single().ShouldStartWith("multipart/form-data");
        var body = request.Body ?? System.Text.Encoding.Latin1.GetString(request.BodyAsBytes!);
        body.ShouldContain("name=\"files[]\"");
        body.ShouldContain("filename=photo.png");
        body.ShouldContain("Content-Type: image/png");
    }

    [Fact]
    public async Task Not_square_stored_nothing_and_is_400()
    {
        Given("POST", 200, """{"data":"notsquare","image":"data:image/png;base64,AAAA"}""");

        (await Writer.SetAsync(Bob, Png, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Theory]
    [InlineData(200, """{"data":{"message":"Unknown filetype"}}""")]
    [InlineData(400, """{"data":{"message":"File is too big"}}""")]
    public async Task A_refusal_with_a_reason_is_400_naming_it(int status, string body)
    {
        Given("POST", status, body);

        var error = (await Writer.SetAsync(Bob, Png, Ct)).Error!;

        error.Kind.ShouldBe(ErrorKind.Validation);
        error.Message.ShouldContain(body.Contains("big", StringComparison.Ordinal) ? "File is too big" : "Unknown filetype");
    }

    [Theory]
    [InlineData(200, "<html>login</html>", ErrorKind.BackendUnavailable)]
    [InlineData(401, "", ErrorKind.BackendUnavailable)]
    [InlineData(403, "", ErrorKind.Forbidden)]
    [InlineData(429, "", ErrorKind.Throttled)]
    [InlineData(500, "", ErrorKind.BackendUnavailable)]
    public async Task Other_answers_map_to_errors(int status, string body, ErrorKind kind)
    {
        Given("POST", status, body);

        (await Writer.SetAsync(Bob, Png, Ct)).Error!.Kind.ShouldBe(kind);
    }

    [Fact]
    public async Task Delete_removes_the_avatar()
    {
        Given("DELETE", 200, "[]");

        (await Writer.DeleteAsync(Bob, Ct)).IsSuccess.ShouldBeTrue();
        _server.LogEntries.ShouldHaveSingleItem().RequestMessage!.Headers!["OCS-APIRequest"].ShouldBe(["true"]);
    }

    [Fact]
    public async Task An_unreachable_server_is_503()
    {
        _server.Stop();

        (await Writer.DeleteAsync(Bob, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public void Outcome_reads_success_only_from_the_status_body() =>
        NextcloudAvatarWriter.Outcome(HttpStatusCode.OK, """{"status":"failure"}""", isDelete: false).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);

    [Fact]
    public async Task A_non_square_photo_is_cropped_before_it_is_sent()
    {
        Given("POST", 200, """{"status":"success"}""");
        using var wide = new SkiaSharp.SKBitmap(40, 20);
        wide.Erase(SkiaSharp.SKColors.Teal);
        using var encoded = wide.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 90);

        (await Writer.SetAsync(Bob, encoded.ToArray(), Ct)).IsSuccess.ShouldBeTrue();

        var sent = _server.LogEntries.ShouldHaveSingleItem().RequestMessage!;
        (sent.Body ?? System.Text.Encoding.Latin1.GetString(sent.BodyAsBytes!)).ShouldContain("filename=photo.jpg");
    }

    [Fact]
    public async Task Bytes_that_are_no_image_never_reach_nextcloud()
    {
        (await Writer.SetAsync(Bob, "GIF89a"u8.ToArray(), Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);

        _server.LogEntries.ShouldBeEmpty();
    }
}

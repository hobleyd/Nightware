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

/// <summary>The photo reader through the adapter's real registration, against a WireMock Nextcloud.</summary>
public sealed class NextcloudAvatarReaderTests : IDisposable
{
    private const string Bob = "bob@nightware.localhost";
    private const string AvatarPath = "/nc/index.php/avatar/bob/512";
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly IDavPrincipalDiscovery _discovery = Substitute.For<IDavPrincipalDiscovery>();
    private readonly IBackendCredentials _credentials = Substitute.For<IBackendCredentials>();
    private readonly ServiceProvider _provider;

    public NextcloudAvatarReaderTests()
    {
        _discovery.DiscoverAsync(new MailboxRef(Bob), Arg.Any<CancellationToken>())
            .Returns(new DavPrincipal("/nc/remote.php/dav/principals/users/bob/", false));
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
        services.AddScoped(_ => _discovery);
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

    private IUserPhotoReader Reader => _provider.CreateScope().ServiceProvider.GetRequiredService<IUserPhotoReader>();

    private void GivenAvatar(int status, string? custom = "1", byte[]? body = null, string contentType = "image/png", string? location = null)
    {
        var response = Response.Create().WithStatusCode(status).WithHeader("Content-Type", contentType).WithBody(body ?? Png);
        if (custom is not null)
        {
            response = response.WithHeader(NextcloudAvatarReader.CustomAvatarHeader, custom);
        }

        if (location is not null)
        {
            response = response.WithHeader("Location", location);
        }

        _server.Given(Request.Create().WithPath(AvatarPath).UsingGet()).RespondWith(response);
    }

    [Fact]
    public void Is_registered_by_the_dav_adapter()
    {
        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IUserPhotoReader>().ShouldBeOfType<NextcloudAvatarReader>();
    }

    [Fact]
    public async Task Returns_an_uploaded_avatar_with_the_callers_groupware_credential()
    {
        GivenAvatar(200, contentType: "image/jpeg");

        var photo = (await Reader.GetAsync(Bob, Ct)).Value;

        photo.ContentType.ShouldBe("image/jpeg");
        photo.Length.ShouldBe(Png.Length);
        await using var stream = await photo.Open(Ct);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, Ct);
        copy.ToArray().ShouldBe(Png);

        var request = _server.LogEntries.ShouldHaveSingleItem().RequestMessage!;
        request.Headers!["Authorization"].ShouldBe(["Bearer nc-token"]);
        request.Path.ShouldBe(AvatarPath, "the uid comes from the discovered principal, under Nextcloud's base path");
    }

    [Theory]
    [InlineData("0")]
    [InlineData(null)]
    public async Task A_generated_letter_avatar_is_no_photo(string? custom)
    {
        GivenAvatar(200, custom);

        (await Reader.GetAsync(Bob, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task An_avatar_over_the_cap_is_no_photo()
    {
        GivenAvatar(200, body: new byte[NextcloudAvatarReader.MaxPhotoBytes + 1]);

        (await Reader.GetAsync(Bob, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Theory]
    [InlineData(404, ErrorKind.NotFound)]
    [InlineData(403, ErrorKind.Forbidden)]
    [InlineData(401, ErrorKind.BackendUnavailable)]
    [InlineData(429, ErrorKind.Throttled)]
    [InlineData(500, ErrorKind.BackendUnavailable)]
    public async Task Failures_map_without_ever_becoming_unauthorized(int status, ErrorKind kind)
    {
        GivenAvatar(status, custom: null);

        (await Reader.GetAsync(Bob, Ct)).Error!.Kind.ShouldBe(kind);
    }

    [Fact]
    public async Task A_login_redirect_is_not_followed()
    {
        GivenAvatar(303, custom: null, location: _server.Url + "/nc/index.php/login");

        (await Reader.GetAsync(Bob, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        _server.LogEntries.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Rooms_and_unknown_principals_have_no_photo()
    {
        _discovery.DiscoverAsync(new MailboxRef("boardroom@nightware.localhost"), Arg.Any<CancellationToken>())
            .Returns(new DavPrincipal("/nc/remote.php/dav/principals/calendar-rooms/calendar_resource_management-boardroom/", false) { CalendarUserType = "ROOM" });
        _discovery.DiscoverAsync(new MailboxRef("x@example.com"), Arg.Any<CancellationToken>()).Returns(Error.NotFound("none"));

        (await Reader.GetAsync("boardroom@nightware.localhost", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await Reader.GetAsync("x@example.com", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _server.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_directory_id_is_not_a_photo_key()
    {
        (await Reader.GetAsync("5d2c6a4e-2f0b-4a8e-9f3c-1b7e2d9a6c11", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task A_service_account_credential_is_refused_before_any_request()
    {
        _credentials.ForAsync(Backend.Groupware, Arg.Any<CancellationToken>())
            .Returns(new BackendCredential("Bearer", "sa", CredentialStrategy.ServiceAccount) { ActAs = Bob });

        (await Reader.GetAsync(Bob, Ct)).Error!.Kind.ShouldBe(ErrorKind.Unsupported);
        _server.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unreachable_nextcloud_is_unavailable()
    {
        _server.Stop();

        (await Reader.GetAsync(Bob, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Theory]
    [InlineData("/remote.php/dav/principals/users/alice/", "alice")]
    [InlineData("/nc/remote.php/dav/principals/users/j%C3%B6rg%20m/", "jörg m")]
    [InlineData("/remote.php/dav/principals/calendar-rooms/x/", null)]
    [InlineData("/remote.php/dav/principals/users/", null)]
    public void Reads_the_uid_from_a_user_principal(string href, string? uid) =>
        NextcloudAvatarReader.UidOf(href).ShouldBe(uid);
}

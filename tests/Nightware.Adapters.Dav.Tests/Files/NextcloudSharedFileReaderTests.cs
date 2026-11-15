using System.Net;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Nightware.Adapters.Dav.Core.Discovery;
using Nightware.Adapters.Dav.Files;
using Nightware.Application.Common.Identity;
using Nightware.Application.Files;
using Nightware.Domain.Files;
using Nightware.Domain.Kernel;

using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Nightware.Adapters.Dav.Tests.Files;

/// <summary>
/// The shared-file reader through the adapter's real registration, against a WireMock Nextcloud serving
/// responses recorded from the dev stack (Nextcloud 35.0.3, <c>Fixtures/Files</c>).
/// </summary>
public sealed class NextcloudSharedFileReaderTests : IDisposable
{
    private const string Bob = "bob@nightware.localhost";
    private const string Token = "jeKXiPKYQXyWQAT";
    private const string SharePath = "/public.php/dav/files/" + Token;
    private const string BobFile = "/remote.php/dav/files/bob/probe.txt";

    private static readonly ShareLink PublicLink = Link("s/" + Token);
    private static readonly ShareLink InternalLink = Link("f/385");

    private readonly WireMockServer _server = WireMockServer.Start();
    private readonly IDavPrincipalDiscovery _discovery = Substitute.For<IDavPrincipalDiscovery>();
    private readonly IBackendCredentials _credentials = Substitute.For<IBackendCredentials>();
    private readonly IUserContext _user = Substitute.For<IUserContext>();
    private readonly ServiceProvider _provider;

    public NextcloudSharedFileReaderTests()
    {
        _user.Principal.Returns(new Principal("bob-sub", Bob, Bob, "Bob"));
        _discovery.DiscoverAsync(new MailboxRef(Bob), Arg.Any<CancellationToken>())
            .Returns(new DavPrincipal("/remote.php/dav/principals/users/bob/", true));
        _credentials.ForAsync(Backend.Groupware, Arg.Any<CancellationToken>())
            .Returns(new BackendCredential("Bearer", "nc-token", CredentialStrategy.TokenExchange));

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Dav:BaseUrl"] = _server.Url + "/",
            ["Dav:MaxRetryAttempts"] = "0",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => _credentials);
        services.AddScoped(_ => _discovery);
        services.AddScoped(_ => _user);
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

    private ISharedFileReader Reader => _provider.CreateScope().ServiceProvider.GetRequiredService<ISharedFileReader>();

    private static ShareLink Link(string sitePath) => new(new Uri("https://cloud.nightware.localhost/" + sitePath), sitePath);

    private static string Recorded(string name) => Fixture.Read(Path.Combine("Files", name));

    private void Serve(string method, string path, int status, string body, string contentType = "application/xml; charset=utf-8") =>
        _server.Given(Request.Create().WithPath(path).UsingMethod(method))
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", contentType).WithBody(body));

    private static async Task<string> ReadAsync(BlobContent content)
    {
        await using var stream = await content.Open(Ct);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(Ct);
    }

    [Fact]
    public void Is_registered_by_the_dav_adapter() =>
        _provider.CreateScope().ServiceProvider.GetRequiredService<ISharedFileReader>().ShouldBeOfType<NextcloudSharedFileReader>();

    [Fact]
    public async Task A_public_file_share_is_read_anonymously_from_public_share_dav()
    {
        Serve("PROPFIND", SharePath, 207, Recorded("public-share-file.propfind.xml"));

        var item = (await Reader.GetItemAsync(PublicLink, Ct)).Value;

        item.ShouldBe(new SharedItem("385", "probe.txt", 13)
        {
            MimeType = "text/plain",
            WebUrl = "https://cloud.nightware.localhost/s/" + Token,
            LastModifiedDateTime = new DateTimeOffset(2026, 11, 15, 3, 57, 33, TimeSpan.Zero),
        });
        var request = _server.LogEntries.Single().RequestMessage!;
        request.Headers!.ContainsKey("Authorization").ShouldBeFalse();
        request.Headers!["Depth"].ShouldBe(["0"]);
        request.Body!.ShouldContain("fileid");
    }

    [Fact]
    public async Task A_public_folder_share_is_a_folder()
    {
        Serve("PROPFIND", "/public.php/dav/files/P9Wz5gq5baRfxd8", 207, Recorded("public-share-folder.propfind.xml"));

        var item = (await Reader.GetItemAsync(Link("s/P9Wz5gq5baRfxd8"), Ct)).Value;

        item.IsFolder.ShouldBeTrue();
        item.Name.ShouldBe("P7probe");
        item.MimeType.ShouldBeNull();
    }

    [Fact]
    public async Task An_unknown_token_is_not_found_although_nextcloud_says_503()
    {
        Serve("PROPFIND", "/public.php/dav/files/nope123nope", 503, Recorded("public-share-unknown.503.xml"));

        (await Reader.GetItemAsync(Link("s/nope123nope"), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task A_genuine_503_stays_unavailable()
    {
        Serve("PROPFIND", SharePath, 503, "<d:error xmlns:d=\"DAV:\"/>");

        (await Reader.GetItemAsync(PublicLink, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task A_password_protected_share_is_forbidden_never_unauthorised()
    {
        Serve("PROPFIND", "/public.php/dav/files/GRc4RNQHMCNoze6", 401, Recorded("public-share-password.401.xml"));

        (await Reader.GetItemAsync(Link("s/GRc4RNQHMCNoze6"), Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task A_public_share_404_is_not_found()
    {
        Serve("PROPFIND", SharePath, 404, string.Empty);

        (await Reader.GetItemAsync(PublicLink, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task An_unreadable_listing_is_unavailable()
    {
        Serve("PROPFIND", SharePath, 207, "<not xml");

        (await Reader.GetItemAsync(PublicLink, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task An_unreachable_server_is_unavailable()
    {
        _server.Stop();

        (await Reader.GetItemAsync(PublicLink, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task Public_share_content_is_streamed_anonymously()
    {
        Serve("GET", SharePath, 200, "hello pdf-ish", "text/plain;charset=UTF-8");

        var content = (await Reader.GetContentAsync(PublicLink, Ct)).Value;

        content.ContentType.ShouldBe("text/plain");
        (await ReadAsync(content)).ShouldBe("hello pdf-ish");
        _server.LogEntries.Single().RequestMessage!.Headers!.ContainsKey("Authorization").ShouldBeFalse();
    }

    [Theory]
    [InlineData(401, ErrorKind.Forbidden)]
    [InlineData(403, ErrorKind.Forbidden)]
    [InlineData(404, ErrorKind.NotFound)]
    [InlineData(429, ErrorKind.Throttled)]
    public async Task Public_share_content_failures_are_mapped(int status, ErrorKind kind)
    {
        Serve("GET", SharePath, status, string.Empty);

        (await Reader.GetContentAsync(PublicLink, Ct)).Error!.Kind.ShouldBe(kind);
    }

    [Fact]
    public async Task Public_share_content_for_an_unknown_token_is_not_found()
    {
        Serve("GET", SharePath, 503, Recorded("public-share-unknown.503.xml"));

        (await Reader.GetContentAsync(PublicLink, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task An_internal_link_is_found_by_file_id_in_the_callers_files()
    {
        Serve("SEARCH", "/remote.php/dav/", 207, Recorded("search-fileid-bob.xml"));

        var item = (await Reader.GetItemAsync(InternalLink, Ct)).Value;

        item.Id.ShouldBe("385");
        item.Name.ShouldBe("probe.txt");
        item.Size.ShouldBe(13);
        item.WebUrl.ShouldBe("https://cloud.nightware.localhost/f/385");
        var request = _server.LogEntries.Single().RequestMessage!;
        request.Headers!["Authorization"].ShouldBe(["Bearer nc-token"]);
        request.Body!.ShouldContain("<d:href>/files/bob</d:href>");
        request.Body!.ShouldContain("<d:literal>385</d:literal>");
    }

    [Fact]
    public async Task An_internal_link_to_nothing_the_caller_can_see_is_not_found()
    {
        Serve("SEARCH", "/remote.php/dav/", 207, Recorded("search-fileid-none.xml"));

        (await Reader.GetItemAsync(InternalLink, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task A_rejected_exchanged_token_is_unavailable_not_a_reauthentication()
    {
        Serve("SEARCH", "/remote.php/dav/", 401, string.Empty);

        (await Reader.GetItemAsync(InternalLink, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task An_unreadable_search_result_is_unavailable()
    {
        Serve("SEARCH", "/remote.php/dav/", 207, "<nope");

        (await Reader.GetItemAsync(InternalLink, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task A_search_against_an_unreachable_server_is_unavailable()
    {
        _server.Stop();

        (await Reader.GetItemAsync(InternalLink, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task A_service_account_credential_is_refused_before_any_request()
    {
        _credentials.ForAsync(Backend.Groupware, Arg.Any<CancellationToken>())
            .Returns(new BackendCredential("Bearer", "svc", CredentialStrategy.ServiceAccount));

        (await Reader.GetItemAsync(InternalLink, Ct)).Error!.Kind.ShouldBe(ErrorKind.Unsupported);
        _server.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Credential_failures_pass_through()
    {
        _credentials.ForAsync(Backend.Groupware, Arg.Any<CancellationToken>()).Returns(Error.BackendAuthFailed("expired"));

        (await Reader.GetItemAsync(InternalLink, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendAuthFailed);
    }

    [Fact]
    public async Task Discovery_failures_pass_through()
    {
        _discovery.DiscoverAsync(new MailboxRef(Bob), Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("down"));

        (await Reader.GetItemAsync(InternalLink, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task A_caller_without_a_user_principal_has_no_files()
    {
        _discovery.DiscoverAsync(new MailboxRef(Bob), Arg.Any<CancellationToken>())
            .Returns(new DavPrincipal("/remote.php/dav/principals/calendar-rooms/x/", true));

        (await Reader.GetItemAsync(InternalLink, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Internal_link_content_is_streamed_from_the_found_href_with_the_callers_credential()
    {
        Serve("SEARCH", "/remote.php/dav/", 207, Recorded("search-fileid-bob.xml"));
        Serve("GET", BobFile, 200, "hello pdf-ish", "text/plain");

        var content = (await Reader.GetContentAsync(InternalLink, Ct)).Value;

        content.ContentType.ShouldBe("text/plain");
        (await ReadAsync(content)).ShouldBe("hello pdf-ish");
        _server.LogEntries.Single(e => e.RequestMessage!.Method == "GET").RequestMessage!.Headers!["Authorization"].ShouldBe(["Bearer nc-token"]);
    }

    [Fact]
    public async Task Internal_link_content_reports_a_failed_search()
    {
        Serve("SEARCH", "/remote.php/dav/", 207, Recorded("search-fileid-none.xml"));

        (await Reader.GetContentAsync(InternalLink, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Internal_link_content_reports_a_failed_download()
    {
        Serve("SEARCH", "/remote.php/dav/", 207, Recorded("search-fileid-bob.xml"));
        Serve("GET", BobFile, 403, string.Empty);

        (await Reader.GetContentAsync(InternalLink, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Theory]
    [InlineData("apps/files/files/385")]
    [InlineData("s/abc/download")]
    public async Task Unrecognised_links_are_not_found_without_a_request(string sitePath)
    {
        (await Reader.GetItemAsync(Link(sitePath), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await Reader.GetContentAsync(Link(sitePath), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _server.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public void The_share_uri_keeps_nextclouds_sub_path_and_escapes_the_token()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Dav:BaseUrl"] = "http://nextcloud/nc/" }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddDavAdapter(configuration);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<NextcloudPublicShares>().ShareUri("a+b").AbsoluteUri.ShouldBe("http://nextcloud/nc/public.php/dav/files/a%2Bb");
    }
}

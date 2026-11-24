using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using Nightware.Adapters.Dav.Core.Discovery;
using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;

using WireMock.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Nightware.Adapters.Dav.Tests.Files.Drive;

/// <summary>
/// The drive adapters through the Dav adapter's real registration, against a WireMock Nextcloud at <c>/cloud/</c> serving
/// responses recorded from the dev stack (Nextcloud 35, alice's home, <c>Fixtures/Files/Drive</c>, scrubbed to example.com).
/// </summary>
internal sealed class DriveAdapterHarness : IDisposable
{
    public const string DavRoot = "/cloud/remote.php/dav/";
    public const string Home = DavRoot + "files/alice/";
    public const string Ocs = "/cloud/ocs/v2.php/apps/";
    public const string Activity = Ocs + "activity/api/v2/activity/files";
    public const string DriveId = "nc!YWxpY2U";

    private readonly ServiceProvider _provider;

    public DriveAdapterHarness()
    {
        var discovery = Discovery;
        discovery.CurrentUserAsync(Arg.Any<CancellationToken>()).Returns(new DavPrincipal(DavRoot + "principals/users/alice/", true));
        var credentials = Substitute.For<IBackendCredentials>();
        credentials.ForAsync(Backend.Groupware, Arg.Any<CancellationToken>()).Returns(new BackendCredential("Bearer", "nc-token", CredentialStrategy.TokenExchange));
        var user = Substitute.For<IUserContext>();
        user.Principal.Returns(new Principal("alice-sub", "alice@example.com", "alice@example.com", "Alice"));

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Dav:BaseUrl"] = Server.Url + "/cloud/",
            ["Dav:MaxRetryAttempts"] = "0",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => credentials);
        services.AddScoped(_ => discovery);
        services.AddScoped(_ => user);
        services.AddSingleton<TimeProvider>(Time);
        services.AddDavAdapter(configuration);
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public WireMockServer Server { get; } = WireMockServer.Start();

    public IDavPrincipalDiscovery Discovery { get; } = Substitute.For<IDavPrincipalDiscovery>();

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 22, 0, 0, 0, TimeSpan.Zero));

    /// <summary>A fresh request scope's service.</summary>
    public T Get<T>()
        where T : notnull =>
        _provider.CreateScope().ServiceProvider.GetRequiredService<T>();

    public static string Recorded(string name) => Fixture.Read(Path.Combine("Files", "Drive", name));

    public IRespondWithAProvider When(string method, string path, string? depth = null, string? bodyContains = null)
    {
        var request = Request.Create().WithPath(path).UsingMethod(method);
        if (depth is not null)
        {
            request = request.WithHeader("Depth", depth);
        }

        if (bodyContains is not null)
        {
            request = request.WithBody(b => b?.Contains(bodyContains, StringComparison.Ordinal) == true);
        }

        return Server.Given(request);
    }

    public void Serve(string method, string path, int status, string body, string? depth = null, string? bodyContains = null, string contentType = "application/xml; charset=utf-8") =>
        When(method, path, depth, bodyContains).RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", contentType).WithBody(body));

    public void Status(string method, string path, int status, string? depth = null) =>
        When(method, path, depth).RespondWith(Response.Create().WithStatusCode(status));

    /// <summary>The recorded home, the folder <c>nwfix</c> (self and listing), <c>nwfix/a.txt</c> by path, and a.txt by id (1748).</summary>
    public void ServeAliceHome()
    {
        Serve("PROPFIND", Home, 207, Recorded("home.propfind.xml"), "0");
        Serve("PROPFIND", Home + "nwfix/", 207, Recorded("folder-self.propfind.xml"), "0");
        Serve("PROPFIND", Home + "nwfix/", 207, Recorded("folder.propfind.xml"), "1");
        Serve("PROPFIND", Home + "nwfix", 207, Recorded("folder-self.propfind.xml"), "0");
        Serve("PROPFIND", Home + "nwfix/a.txt", 207, Recorded("file.propfind.xml"), "0");
        Serve("SEARCH", DavRoot, 207, Recorded("search-by-id.xml"), bodyContains: "<d:literal>1748</d:literal>");
    }

    public IEnumerable<ILogEntry> Requests(string method, string? path = null) =>
        Server.LogEntries.Where(e => e.RequestMessage!.Method == method && (path is null || e.RequestMessage.Path == path));

    public void Dispose()
    {
        _provider.Dispose();
        Server.Stop();
        Server.Dispose();
    }

    public const string EmptyMultistatus = "<?xml version=\"1.0\"?><d:multistatus xmlns:d=\"DAV:\"/>";
}

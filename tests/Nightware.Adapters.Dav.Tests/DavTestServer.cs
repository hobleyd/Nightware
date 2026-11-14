using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Core.Http;
using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;
using WireMock.Server;

namespace Nightware.Adapters.Dav.Tests;

/// <summary>A WireMock server standing in for Nextcloud, plus a <see cref="DavClient"/> wired to it.</summary>
internal sealed class DavTestServer : IDisposable
{
    public const string Token = "token-alice";

    public DavTestServer(string basePath = "")
    {
        Server = WireMockServer.Start();
        Options = new DavOptions { BaseUrl = new Uri(Server.Url! + basePath + "/") };
        Paths = new DavPaths(Microsoft.Extensions.Options.Options.Create(Options));
        Credentials = Substitute.For<IBackendCredentials>();
        Credentials.ForAsync(Backend.Groupware, Arg.Any<CancellationToken>())
            .Returns(Result<BackendCredential>.Success(new BackendCredential("Bearer", Token, CredentialStrategy.TokenExchange)));
        Time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero));
        Http = new HttpClient(new DavAuthenticationHandler { InnerHandler = new HttpClientHandler() });
        Client = new DavClient(Http, Credentials, Paths, Time);
    }

    public WireMockServer Server { get; }

    public DavOptions Options { get; }

    public DavPaths Paths { get; }

    public IBackendCredentials Credentials { get; }

    public FakeTimeProvider Time { get; }

    public HttpClient Http { get; }

    public DavClient Client { get; }

    /// <summary>Server-absolute DAV root, e.g. <c>/remote.php/dav/</c>.</summary>
    public string Root => Paths.RootPath;

    public void Dispose()
    {
        Http.Dispose();
        Server.Stop();
        Server.Dispose();
    }
}

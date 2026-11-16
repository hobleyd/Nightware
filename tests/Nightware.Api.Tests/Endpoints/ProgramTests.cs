using System.Net;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

using Nightware.Api.Tests.Support;

namespace Nightware.Api.Tests.Endpoints;

/// <summary>The real <c>Program</c> with its own appsettings.</summary>
public sealed class ProgramTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ProgramTests(WebApplicationFactory<Program> factory) =>
        _factory = factory.WithWebHostBuilder(static b => b.UseEnvironment("Development"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Discovery_document_is_anonymous_and_complete()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/.well-known/nightware", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadObjectAsync();
        body.Select(p => p.Key).Order().ShouldBe(["authorizationEndpoint", "clientId", "graphBaseUrl", "graphBetaBaseUrl", "scopes", "tokenEndpoint"]);
        body.Str("graphBaseUrl").ShouldBe("https://nightware.localhost/graph/v1.0");
        body.Str("graphBetaBaseUrl").ShouldBe("https://nightware.localhost/graph/beta");
        body.Str("clientId").ShouldBe("nightmail");
        body["scopes"]!.AsArray().Select(s => s!.GetValue<string>()).ShouldContain("offline_access");
        foreach (var key in new[] { "graphBaseUrl", "graphBetaBaseUrl", "authorizationEndpoint", "tokenEndpoint" })
        {
            body.Str(key)!.ShouldStartWith("https://");
        }
    }

    [Fact]
    public async Task Discovery_reflects_configuration()
    {
        using var client = _factory.WithWebHostBuilder(static b => b.ConfigureAppConfiguration(static (_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["Discovery:ClientId"] = "custom" }))).CreateClient();

        (await (await client.GetAsync("/.well-known/nightware", Ct)).ReadObjectAsync()).Str("clientId").ShouldBe("custom");
    }

    [Fact]
    public async Task Discovery_advertises_file_sites_only_when_configured()
    {
        using var client = _factory.WithWebHostBuilder(static b => b.ConfigureAppConfiguration(static (_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Discovery:FilesBaseUrls:0"] = "https://nightware.localhost/cloud",
                ["Discovery:FilesBaseUrls:1"] = "https://files.example.com/nextcloud",
            }))).CreateClient();

        var body = await (await client.GetAsync("/.well-known/nightware", Ct)).ReadObjectAsync();

        body["filesBaseUrls"]!.AsArray().Select(s => s!.GetValue<string>()).ShouldBe(["https://nightware.localhost/cloud", "https://files.example.com/nextcloud"]);
    }

    [Theory]
    [InlineData("cloud.example.com")]
    [InlineData("ftp://cloud.example.com")]
    [InlineData("https://cloud.example.com/?x=1")]
    [InlineData("https://user:pw@cloud.example.com")]
    public void An_invalid_file_site_fails_at_startup(string url)
    {
        var error = ProgramStartup.Failure(() => _factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["Discovery:FilesBaseUrls:0"] = url }))));

        error.ToString().ShouldContain("FilesBaseUrls");
    }

    [Fact]
    public async Task Health_is_anonymous()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/healthz", Ct);

        response.StatusCode.ShouldNotBe(HttpStatusCode.Unauthorized);
        response.StatusCode.ShouldNotBe(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData("/v1.0/me/mailFolders")]
    [InlineData("/beta/me/findRooms")]
    public async Task Graph_routes_require_a_token(string path)
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(path, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await response.ReadObjectAsync()).Str("error.code").ShouldBe("InvalidAuthenticationToken");
    }

    [Fact]
    public void Invalid_discovery_configuration_fails_at_startup()
    {
        var broken = _factory.WithWebHostBuilder(static b => b.ConfigureAppConfiguration(static (_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["Discovery:GraphBaseUrl"] = string.Empty })));

        Should.Throw<Exception>(() => broken.CreateClient());
    }
}

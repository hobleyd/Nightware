using Microsoft.Extensions.DependencyInjection;

using Nightware.Adapters.Dav.Administration;
using Nightware.Application.Administration;
using Nightware.Domain.Kernel;

using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Nightware.Adapters.Dav.Tests.Administration;

/// <summary>The transport: URL building, headers, retries, cookies, logging and registration.</summary>
public sealed class NextcloudProvisioningClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("")]
    [InlineData("/cloud")]
    [InlineData("/apps/nc")]
    public async Task Paths_go_below_a_prefixed_base_url(string basePath)
    {
        using var nextcloud = new NextcloudAdminServer(basePath);
        nextcloud.Answer("GET", "/ocs/v1.php/cloud/user", 200, 100, """{"id":"nightware-admin"}""");

        var reply = (await nextcloud.Get<NextcloudProvisioningClient>().SendAsync(HttpMethod.Get, "ocs/v1.php/cloud/user", null, Ct)).Value;

        reply.Succeeded.ShouldBeTrue();
        nextcloud.Requests.Single().RequestMessage!.AbsolutePath.ShouldBe(basePath + "/ocs/v1.php/cloud/user");
    }

    [Theory]
    [InlineData("http://nc.test/cloud", "http://nc.test/cloud/ocs/v1.php/cloud/users/a%40b?format=json")]
    [InlineData("http://nc.test/cloud/", "http://nc.test/cloud/ocs/v1.php/cloud/users/a%40b?format=json")]
    [InlineData("http://nc.test", "http://nc.test/ocs/v1.php/cloud/users/a%40b?format=json")]
    public void Urls_keep_the_base_path_and_the_escaping(string baseUrl, string expected)
    {
        var client = Client(baseUrl);

        client.Url("ocs/v1.php/cloud/users/" + Uri.EscapeDataString("a@b")).AbsoluteUri.ShouldBe(expected);
    }

    [Fact]
    public async Task Get_is_retried_on_a_transient_failure()
    {
        using var nextcloud = new NextcloudAdminServer();
        nextcloud.Server.Given(Request.Create().WithPath("/ocs/v1.php/cloud/user").UsingGet()).InScenario("flaky").WillSetStateTo("recovered")
            .RespondWith(Response.Create().WithStatusCode(503));
        nextcloud.Server.Given(Request.Create().WithPath("/ocs/v1.php/cloud/user").UsingGet()).InScenario("flaky").WhenStateIs("recovered")
            .RespondWith(Response.Create().WithStatusCode(200).WithBody(NextcloudAdminServer.Envelope(100, """{"id":"nightware-admin"}""")));

        var reply = (await nextcloud.Get<NextcloudProvisioningClient>().SendAsync(HttpMethod.Get, "ocs/v1.php/cloud/user", null, Ct)).Value;

        reply.Succeeded.ShouldBeTrue();
        nextcloud.Requests.Count().ShouldBe(2);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task Writes_are_sent_once(string method)
    {
        using var nextcloud = new NextcloudAdminServer();
        nextcloud.AnswerRaw(method, "/ocs/v1.php/cloud/users/alice", 503, "");

        var reply = (await nextcloud.Get<NextcloudProvisioningClient>().SendAsync(new HttpMethod(method), "ocs/v1.php/cloud/users/alice", null, Ct)).Value;

        reply.Succeeded.ShouldBeFalse();
        nextcloud.Requests.Count().ShouldBe(1);
    }

    [Fact]
    public async Task Redirects_are_not_followed()
    {
        using var nextcloud = new NextcloudAdminServer();
        nextcloud.Server.Given(Request.Create().WithPath("/ocs/v1.php/cloud/user").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(302).WithHeader("Location", nextcloud.Server.Url + "/login"));

        var reply = (await nextcloud.Get<NextcloudProvisioningClient>().SendAsync(HttpMethod.Get, "ocs/v1.php/cloud/user", null, Ct)).Value;

        reply.HttpStatus.ShouldBe(302);
        reply.ToError("x").Kind.ShouldBe(ErrorKind.BackendUnavailable);
        nextcloud.Requests.Count().ShouldBe(1);
    }

    [Fact]
    public async Task Session_cookies_are_never_replayed()
    {
        using var nextcloud = new NextcloudAdminServer();
        nextcloud.Server.Given(Request.Create().WithPath("/ocs/v1.php/cloud/user").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Set-Cookie", "oc_sessionPassphrase=abc; path=/").WithBody(NextcloudAdminServer.Envelope(100, "{}")));
        var client = nextcloud.Get<NextcloudProvisioningClient>();

        await client.SendAsync(HttpMethod.Get, "ocs/v1.php/cloud/user", null, Ct);
        await client.SendAsync(HttpMethod.Get, "ocs/v1.php/cloud/user", null, Ct);

        nextcloud.Requests.ShouldAllBe(e => e.RequestMessage!.Headers == null || !e.RequestMessage.Headers.ContainsKey("Cookie"));
    }

    [Fact]
    public void The_primary_handler_is_cookie_free()
    {
        using var nextcloud = new NextcloudAdminServer();
        var handler = nextcloud.Get<IHttpMessageHandlerFactory>().CreateHandler(nameof(NextcloudProvisioningClient));
        while (handler is DelegatingHandler { InnerHandler: { } inner })
        {
            handler = inner;
        }

        var sockets = handler.ShouldBeOfType<SocketsHttpHandler>();
        sockets.UseCookies.ShouldBeFalse();
        sockets.AllowAutoRedirect.ShouldBeFalse();
    }

    [Fact]
    public async Task The_password_never_reaches_the_logs()
    {
        using var nextcloud = new NextcloudAdminServer();
        nextcloud.Answer("GET", "/ocs/v1.php/cloud/users/alice", 401, 997, message: "Unauthorised");
        nextcloud.AnswerRaw("PUT", "/ocs/v1.php/cloud/users/alice/disable", 500, "boom", "text/plain");
        var accounts = nextcloud.Get<IGroupwareAccounts>();

        await accounts.FindAsync("alice", Ct);
        await accounts.SetEnabledAsync("alice", false, Ct);
        await nextcloud.Get<IBackendStatus>().CheckAsync(Ct);

        nextcloud.Logs.Lines.ShouldNotBeEmpty();
        nextcloud.Logs.AllText.ShouldNotContain(NextcloudAdminServer.Password);
        nextcloud.Logs.AllText.ShouldNotContain(NextcloudAdminServer.BasicHeader["Basic ".Length..]);
    }

    [Fact]
    public async Task A_slow_server_times_out_as_unavailable()
    {
        using var nextcloud = new NextcloudAdminServer(settings: new()
        {
            ["NextcloudProvisioning:AttemptTimeout"] = "00:00:00.200",
            ["NextcloudProvisioning:TotalTimeout"] = "00:00:00.500",
            ["NextcloudProvisioning:MaxRetryAttempts"] = "0",
        });
        nextcloud.Server.Given(Request.Create().WithPath("/ocs/v1.php/cloud/user").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(3)));

        var result = await nextcloud.Get<NextcloudProvisioningClient>().SendAsync(HttpMethod.Get, "ocs/v1.php/cloud/user", null, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public void Registration_is_self_contained()
    {
        // Only NextcloudProvisioning:* — no Dav:, no AddDavAdapter (the admin host registers neither).
        using var nextcloud = new NextcloudAdminServer();

        nextcloud.Get<IGroupwareAccounts>().ShouldBeOfType<NextcloudGroupwareAccounts>();
        nextcloud.Provider.GetServices<IBackendStatus>().ShouldHaveSingleItem().ShouldBeOfType<NextcloudBackendStatus>();
    }

    [Fact]
    public void Registering_twice_adds_one_status_check()
    {
        using var nextcloud = new NextcloudAdminServer();
        var services = new ServiceCollection().AddLogging();
        services.AddNextcloudAdministration(nextcloud.Configuration);
        services.AddNextcloudAdministration(nextcloud.Configuration);
        using var provider = services.BuildServiceProvider();

        provider.GetServices<IBackendStatus>().Count().ShouldBe(1);
    }

    [Fact]
    public void Invalid_configuration_fails_at_startup()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddNextcloudAdministration(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        Should.Throw<Microsoft.Extensions.Options.OptionsValidationException>(() =>
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<NextcloudProvisioningOptions>>().Value);
    }

    [Fact]
    public async Task Ocs_failures_are_logged_with_their_codes()
    {
        using var nextcloud = new NextcloudAdminServer();
        nextcloud.Answer("GET", "/ocs/v1.php/cloud/users/alice", 200, 998);

        await nextcloud.Get<NextcloudProvisioningClient>().SendAsync(HttpMethod.Get, "ocs/v1.php/cloud/users/alice", null, Ct);

        nextcloud.Logs.Lines.ShouldContain(l => l.Contains("OCS 998", StringComparison.Ordinal));
    }

    private static NextcloudProvisioningClient Client(string baseUrl) => new(
        new HttpClient(),
        Microsoft.Extensions.Options.Options.Create(new NextcloudProvisioningOptions { BaseUrl = new Uri(baseUrl), Username = "u", Password = "p" }),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<NextcloudProvisioningClient>.Instance);
}

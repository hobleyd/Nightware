using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Nightware.Adapters.Dav.Calendar.ICal;
using Nightware.Adapters.Dav.Contacts.VCard;
using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Core.Collections;
using Nightware.Adapters.Dav.Core.Discovery;
using Nightware.Adapters.Dav.Tasks.ICal;
using Nightware.Application.Calendar;
using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Nightware.Adapters.Dav.Tests;

public sealed class DavAdapterRegistrationTests : IDisposable
{
    private readonly WireMockServer _server = WireMockServer.Start();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ServiceProvider Build(string? token = "alice-token", int retries = 2)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Dav:BaseUrl"] = _server.Url + "/",
            ["Dav:MaxRetryAttempts"] = retries.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Dav:HealthCheckTimeout"] = "00:00:01",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        var credentials = Substitute.For<IBackendCredentials>();
        credentials.ForAsync(Backend.Groupware, Arg.Any<CancellationToken>())
            .Returns(_ => Result<BackendCredential>.Success(new BackendCredential("Bearer", CurrentToken ?? token!, CredentialStrategy.TokenExchange)));
        services.AddScoped(_ => credentials);
        services.AddDavAdapter(configuration);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    private string? CurrentToken { get; set; }

    [Fact]
    public void Registers_core_services_and_codecs()
    {
        using var provider = Build();
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IDavClient>().ShouldBeOfType<DavClient>();
        scope.ServiceProvider.GetRequiredService<IDavPrincipalDiscovery>().ShouldBeOfType<DavPrincipalDiscovery>();
        scope.ServiceProvider.GetRequiredService<IDavCollectionCatalog>().ShouldBeOfType<DavCollectionCatalog>();
        provider.GetRequiredService<IICalendarCodec>().ShouldBeSameAs(provider.GetRequiredService<IcalNetCalendarCodec>());
        provider.GetRequiredService<IcsEditor>().ShouldNotBeNull();
        provider.GetRequiredService<FreeBusyCodec>().ShouldNotBeNull();
        provider.GetRequiredService<VTodoCodec>().ShouldNotBeNull();
        provider.GetRequiredService<VCardCodec>().ShouldNotBeNull();
        provider.GetRequiredService<DavPaths>().RootPath.ShouldBe("/remote.php/dav/");
    }

    [Fact]
    public void Invalid_options_fail_validation()
    {
        var services = new ServiceCollection();
        services.AddDavAdapter(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<DavOptions>>().Value);
    }

    [Fact]
    public async Task Safe_methods_are_retried_on_transient_failures()
    {
        _server.Given(Request.Create().WithPath("/remote.php/dav/x.ics").UsingGet()).InScenario("flaky").WillSetStateTo("recovered")
            .RespondWith(Response.Create().WithStatusCode(503));
        _server.Given(Request.Create().WithPath("/remote.php/dav/x.ics").UsingGet()).InScenario("flaky").WhenStateIs("recovered")
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("ok"));
        using var provider = Build();
        using var scope = provider.CreateScope();

        var result = await scope.ServiceProvider.GetRequiredService<IDavClient>().GetAsync("x.ics", Ct);

        result.Value.Body.ShouldBe("ok");
        _server.LogEntries.Count.ShouldBe(2);
        _server.LogEntries.ShouldAllBe(e => e.RequestMessage!.Headers!["Authorization"][0] == "Bearer alice-token");
    }

    [Fact]
    public async Task Propfind_and_report_are_retried()
    {
        _server.Given(Request.Create().WithPath("/remote.php/dav/c/").UsingMethod("REPORT")).RespondWith(Response.Create().WithStatusCode(502));
        using var provider = Build(retries: 2);
        using var scope = provider.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IDavClient>().ReportAsync("c/", DavDepth.One, DavRequestBodies.CalendarMultiget(["/remote.php/dav/c/a.ics"]), Ct);
        result.Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        _server.LogEntries.Count.ShouldBe(3);
        _server.LogEntries.Select(e => e.RequestMessage!.Body).Distinct().ShouldHaveSingleItem()!.ShouldContain("calendar-multiget");
    }

    [Theory]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("MKCOL")]
    [InlineData("MKCALENDAR")]
    [InlineData("MOVE")]
    [InlineData("PROPPATCH")]
    [InlineData("POST")]
    public async Task Unsafe_methods_are_never_retried(string method)
    {
        _server.Given(Request.Create().WithPath("/remote.php/dav/c/x").UsingMethod(method)).RespondWith(Response.Create().WithStatusCode(503));
        using var provider = Build();
        using var scope = provider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IDavClient>();
        Task<Result> call = method switch
        {
            "PUT" => client.PutAsync("c/x", "body", "text/plain", DavPrecondition.None, Ct).ContinueWith(t => t.Result.ToResult(), TaskScheduler.Default),
            "DELETE" => client.DeleteAsync("c/x", DavPrecondition.None, Ct),
            "MKCOL" => client.MkColAsync("c/x", null, Ct),
            "MKCALENDAR" => client.MkCalendarAsync("c/x", "x", ["VEVENT"], null, Ct),
            "MOVE" => client.MoveAsync("c/x", "c/y", true, Ct),
            "PROPPATCH" => client.PropPatchAsync("c/x", [], [DavNames.DisplayName], Ct).ContinueWith(t => t.Result.ToResult(), TaskScheduler.Default),
            _ => client.PostToOutboxAsync("c/x", "BEGIN:VCALENDAR", Ct).ContinueWith(t => t.Result.ToResult(), TaskScheduler.Default),
        };
        (await call).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        _server.LogEntries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Pooled_handlers_send_each_scope_its_own_credential()
    {
        _server.Given(Request.Create().WithPath("/remote.php/dav/x.ics").UsingGet()).RespondWith(Response.Create().WithStatusCode(200).WithBody("ok"));
        using var provider = Build();
        foreach (var token in new[] { "alice-token", "bob-token" })
        {
            CurrentToken = token;
            using var scope = provider.CreateScope();
            (await scope.ServiceProvider.GetRequiredService<IDavClient>().GetAsync("x.ics", Ct)).IsSuccess.ShouldBeTrue();
        }

        _server.LogEntries.Select(e => e.RequestMessage!.Headers!["Authorization"][0]).ShouldBe(["Bearer alice-token", "Bearer bob-token"]);
    }

    [Theory]
    [InlineData(207, HealthStatus.Healthy)]
    [InlineData(401, HealthStatus.Healthy)]
    [InlineData(200, HealthStatus.Healthy)]
    [InlineData(503, HealthStatus.Unhealthy)]
    [InlineData(500, HealthStatus.Unhealthy)]
    [InlineData(404, HealthStatus.Unhealthy)]
    public async Task Health_check_probes_the_dav_root_anonymously(int status, HealthStatus expected)
    {
        _server.Given(Request.Create().WithPath("/remote.php/dav/").UsingMethod("PROPFIND")).RespondWith(Response.Create().WithStatusCode(status));
        using var provider = Build();

        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(r => r.Tags.Contains(DavHealthProbe.Tag), Ct);

        report.Entries[DavHealthProbe.Name].Status.ShouldBe(expected);
        var request = _server.LogEntries.Single().RequestMessage!;
        request.Headers!.ContainsKey("Authorization").ShouldBeFalse();
        request.Headers["Depth"][0].ShouldBe("0");
    }

    [Fact]
    public async Task Health_check_reports_timeouts_and_unreachable_servers()
    {
        _server.Given(Request.Create().WithPath("/remote.php/dav/").UsingMethod("PROPFIND")).RespondWith(Response.Create().WithStatusCode(207).WithDelay(TimeSpan.FromSeconds(3)));
        using var provider = Build();
        var probe = provider.GetRequiredService<DavHealthProbe>();
        (await probe.CheckAsync(Ct)).IsHealthy.ShouldBeFalse();

        _server.Stop();
        var unreachable = await probe.CheckAsync(Ct);
        unreachable.IsHealthy.ShouldBeFalse();
        unreachable.Description.ShouldContain("unreachable");
    }

    public void Dispose() => _server.Dispose();
}

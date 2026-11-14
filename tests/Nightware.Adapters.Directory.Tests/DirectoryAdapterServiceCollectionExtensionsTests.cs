using System.Text;

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

using Nightware.Adapters.Directory.Core;
using Nightware.Adapters.Directory.Core.Admin;
using Nightware.Adapters.Directory.Credentials;
using Nightware.Adapters.Directory.Tests.TestSupport;
using Nightware.Application.Common.Identity;

using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Nightware.Adapters.Directory.Tests;

public sealed class DirectoryAdapterServiceCollectionExtensionsTests : IDisposable
{
    private readonly KeycloakStub _keycloak = new();

    public void Dispose() => _keycloak.Dispose();

    [Fact]
    public async Task Container_validates_with_scope_checks_and_lifetimes_are_right()
    {
        // AdapterHost builds with ValidateScopes + ValidateOnBuild: a captive dependency would throw here.
        await using AdapterHost host = AdapterHost.Create(_keycloak.Url);

        host.Get<IKeycloakTokenClient>().ShouldBeSameAs(host.Get<IKeycloakTokenClient>());
        host.Get<IMemoryCache>().ShouldNotBeNull();
        Should.Throw<InvalidOperationException>(() => host.Provider.GetRequiredService<IBackendCredentials>()); // scoped, not from root

        using IServiceScope a = host.Provider.CreateScope();
        using IServiceScope b = host.Provider.CreateScope();
        IBackendCredentials inA = a.ServiceProvider.GetRequiredService<IBackendCredentials>();
        inA.ShouldBeSameAs(a.ServiceProvider.GetRequiredService<IBackendCredentials>());
        inA.ShouldNotBeSameAs(b.ServiceProvider.GetRequiredService<IBackendCredentials>());
        a.ServiceProvider.GetRequiredService<IKeycloakAdminClient>().ShouldNotBeNull();
    }

    [Fact]
    public async Task Binds_both_configuration_sections()
    {
        await using AdapterHost host = AdapterHost.Create(_keycloak.Url, new Dictionary<string, string?>
        {
            ["BackendCredentials:Mail:Strategy"] = "ServiceAccount",
            ["BackendCredentials:Mail:ServiceAccount:Mode"] = "ClientCredentials",
            ["BackendCredentials:Cache:ExpirySkew"] = "00:01:00",
        });

        DirectoryOptions directory = host.Get<IOptions<DirectoryOptions>>().Value;
        BackendCredentialOptions credentials = host.Get<IOptions<BackendCredentialOptions>>().Value;

        directory.Realm.ShouldBe("nightware");
        directory.ClientId.ShouldBe("nightware-api");
        credentials.Mail.Strategy.ShouldBe(CredentialStrategy.ServiceAccount);
        credentials.Mail.Audience.ShouldBe("stalwart"); // default kept when not overridden
        credentials.Groupware.Audience.ShouldBe("nextcloud");
        credentials.Cache.ExpirySkew.ShouldBe(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Missing_directory_configuration_fails_validation_on_first_use()
    {
        await using AdapterHost host = AdapterHost.Create(_keycloak.Url, new Dictionary<string, string?> { ["Directory:ClientSecret"] = "" });

        Should.Throw<OptionsValidationException>(() => host.Get<IOptions<DirectoryOptions>>().Value)
            .Message.ShouldContain("ClientSecret");
    }

    [Fact]
    public async Task Registers_the_keycloak_health_check_with_the_backend_tag()
    {
        await using AdapterHost host = AdapterHost.Create(_keycloak.Url);

        HealthCheckServiceOptions options = host.Get<IOptions<HealthCheckServiceOptions>>().Value;

        options.Registrations.ShouldContain(r => r.Name == "keycloak" && r.Tags.Contains(DirectoryAdapterServiceCollectionExtensions.BackendHealthTag));
    }

    [Fact]
    public void Requires_configuration() =>
        Should.Throw<ArgumentNullException>(() => new ServiceCollection().AddDirectoryAdapter(null!));

    [Fact]
    public async Task No_token_or_secret_material_reaches_the_logs()
    {
        // Every path that handles a credential, at Trace level, across all log categories
        // (including System.Net.Http.HttpClient.* and Polly).
        const string password = "stalwart-master-password";
        string subject = Jwt.WithExpiry(DateTimeOffset.UtcNow.AddMinutes(10));
        string rejectedSubject = Jwt.WithExpiry(DateTimeOffset.UtcNow.AddMinutes(10), subject: "revoked");
        _keycloak.GivenServiceAccountToken();
        _keycloak.GivenExchange("nextcloud", "exchanged-nextcloud-token");
        _keycloak.Server.Given(Request.Create().WithPath(KeycloakStub.TokenPath).UsingPost()
                .WithBody(b => b?.Contains(Uri.EscapeDataString(rejectedSubject), StringComparison.Ordinal) == true || b?.Contains(rejectedSubject, StringComparison.Ordinal) == true))
            .AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(400).WithBody(Fixture.Load("error-invalid-token.json")));
        _keycloak.Server.Given(Request.Create().WithPath(KeycloakStub.AdminPath + "/users/u1").UsingGet().WithHeader("Authorization", "Bearer sa-token-1"))
            .AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(401));
        _keycloak.Server.Given(Request.Create().WithPath(KeycloakStub.AdminPath + "/users/u1").UsingGet())
            .AtPriority(10)
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("""{"id":"u1"}"""));

        await using AdapterHost host = AdapterHost.Create(_keycloak.Url, new Dictionary<string, string?>
        {
            ["BackendCredentials:Mail:Strategy"] = "ServiceAccount",
            ["BackendCredentials:Mail:ServiceAccount:Username"] = "admin",
            ["BackendCredentials:Mail:ServiceAccount:Password"] = password,
        });
        host.User.BearerToken = subject;
        CancellationToken ct = TestContext.Current.CancellationToken;

        using (IServiceScope scope = host.Provider.CreateScope())
        {
            IBackendCredentials credentials = scope.ServiceProvider.GetRequiredService<IBackendCredentials>();
            BackendCredential groupware = (await credentials.ForAsync(Backend.Groupware, ct)).Value;
            BackendCredential mail = (await credentials.ForAsync(Backend.Mail, ct)).Value;
            BackendCredential directory = (await credentials.ForAsync(Backend.Directory, ct)).Value;
            (await scope.ServiceProvider.GetRequiredService<IKeycloakAdminClient>().GetUserAsync("u1", ct)).IsSuccess.ShouldBeTrue();

            // ToString is what a careless log line would print.
            groupware.ToString().ShouldNotContain("exchanged-nextcloud-token");
            mail.ToString().ShouldNotContain(mail.Parameter);
            directory.ToString().ShouldNotContain("sa-token");
        }

        host.User.BearerToken = rejectedSubject;
        using (IServiceScope scope = host.Provider.CreateScope())
        {
            (await scope.ServiceProvider.GetRequiredService<IBackendCredentials>().ForAsync(Backend.Groupware, ct)).IsSuccess.ShouldBeFalse();
        }

        string logs = host.Logs.AllText;
        logs.ShouldContain("System.Net.Http.HttpClient."); // the factory's request/response header logging is covered
        logs.ShouldContain("Authorization: *"); // …and it redacts header values
        logs.ShouldContain("Polly");
        string[] secrets =
        [
            subject,
            rejectedSubject,
            Uri.EscapeDataString(subject),
            "exchanged-nextcloud-token",
            "sa-token-1",
            "sa-token-2",
            KeycloakStub.ClientSecret,
            password,
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"admin:{password}")),
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{KeycloakStub.ClientId}:{KeycloakStub.ClientSecret}")),
        ];
        foreach (string secret in secrets)
        {
            logs.ShouldNotContain(secret, Case.Sensitive);
        }

        // Signature segment of the JWTs on their own, too.
        logs.ShouldNotContain(subject.Split('.')[2], Case.Sensitive);
        // exchange, client-credentials, admin 401, client-credentials refresh, admin 200, rejected exchange
        _keycloak.Server.LogEntries.Select(static e => e.ResponseMessage?.StatusCode?.ToString()).ShouldBe(["200", "200", "401", "200", "200", "400"]);
        host.Logs.Lines.ShouldContain(l => l.Contains("rejected the subject token", StringComparison.Ordinal));
    }
}

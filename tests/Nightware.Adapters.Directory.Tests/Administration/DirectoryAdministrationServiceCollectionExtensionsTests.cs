using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

using Nightware.Adapters.Directory.Administration;
using Nightware.Adapters.Directory.Core;
using Nightware.Adapters.Directory.Core.Admin;
using Nightware.Adapters.Directory.Credentials;
using Nightware.Adapters.Directory.Tests.TestSupport;
using Nightware.Application.Administration;
using Nightware.Application.Common.Identity;
using Nightware.Application.Directory;

namespace Nightware.Adapters.Directory.Tests.Administration;

/// <summary>The admin console's composition: Keycloak core + administration ports, no Graph pieces.</summary>
public sealed class DirectoryAdministrationServiceCollectionExtensionsTests : IDisposable
{
    private readonly KeycloakStub _keycloak = new();

    public void Dispose() => _keycloak.Dispose();

    [Fact]
    public async Task Console_container_validates_without_a_user_context_and_binds_its_own_client()
    {
        // CreateConsole builds with ValidateScopes + ValidateOnBuild and registers no IUserContext.
        await using AdapterHost host = AdapterHost.CreateConsole(_keycloak.Url);

        host.Get<IOptions<DirectoryOptions>>().Value.ClientId.ShouldBe("nightware-admin");
        host.Get<IKeycloakTokenClient>().ShouldBeSameAs(host.Get<IKeycloakTokenClient>());
        host.Provider.GetService<IUserContext>().ShouldBeNull();

        using IServiceScope scope = host.Provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IEntityRegistry>().ShouldBeOfType<KeycloakEntityRegistry>();
        scope.ServiceProvider.GetRequiredService<IIdentityAccounts>().ShouldBeOfType<KeycloakIdentityAccounts>();
        scope.ServiceProvider.GetRequiredService<IAdminScopeSource>().ShouldBeOfType<KeycloakAdminScopeSource>();
        scope.ServiceProvider.GetServices<IBackendStatus>().ShouldHaveSingleItem().ShouldBeOfType<KeycloakBackendStatus>();
        scope.ServiceProvider.GetRequiredService<IEntityRegistry>().ShouldBeSameAs(scope.ServiceProvider.GetRequiredService<IEntityRegistry>());
    }

    [Fact]
    public async Task Console_container_has_none_of_the_graph_hosts_ports()
    {
        await using AdapterHost host = AdapterHost.CreateConsole(_keycloak.Url);
        using IServiceScope scope = host.Provider.CreateScope();

        scope.ServiceProvider.GetService<IBackendCredentials>().ShouldBeNull();
        scope.ServiceProvider.GetService<IUserDirectory>().ShouldBeNull();
    }

    [Fact]
    public async Task Console_container_registers_the_keycloak_health_check()
    {
        await using AdapterHost host = AdapterHost.CreateConsole(_keycloak.Url);

        host.Get<IOptions<HealthCheckServiceOptions>>().Value.Registrations
            .ShouldContain(r => r.Name == "keycloak" && r.Tags.Contains(DirectoryAdapterServiceCollectionExtensions.BackendHealthTag));
    }

    [Fact]
    public void Backend_status_stays_enumerable_next_to_other_backends()
    {
        ServiceCollection services = new();
        services.AddScoped<IBackendStatus, OtherBackend>();

        services.AddKeycloakAdministration();
        services.AddKeycloakAdministration(); // idempotent

        services.Count(static d => d.ServiceType == typeof(IBackendStatus)).ShouldBe(2);
        services.Count(static d => d.ServiceType == typeof(IEntityRegistry)).ShouldBe(1);
    }

    [Fact]
    public void Core_requires_configuration() =>
        Should.Throw<ArgumentNullException>(() => new ServiceCollection().AddKeycloakCore(null!));

    [Fact]
    public void Core_alone_registers_no_graph_or_console_ports()
    {
        ServiceCollection services = new();
        services.AddKeycloakCore(new ConfigurationBuilder().Build());

        services.ShouldContain(static d => d.ServiceType == typeof(IKeycloakAdminClient));
        services.ShouldContain(static d => d.ServiceType == typeof(IKeycloakTokenClient));
        services.ShouldNotContain(static d => d.ServiceType == typeof(IBackendCredentials));
        services.ShouldNotContain(static d => d.ServiceType == typeof(IEntityRegistry));
    }

    private sealed class OtherBackend : IBackendStatus
    {
        public Task<BackendHealth> CheckAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new BackendHealth(Nightware.Domain.Administration.BackendKind.Mail, true));
    }
}

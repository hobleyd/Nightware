using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Nightware.Adapters.Jmap.Administration;
using Nightware.Adapters.Jmap.Tests.Support;
using Nightware.Application.Administration;
using Nightware.Application.Common.Identity;

namespace Nightware.Adapters.Jmap.Tests.Administration;

public sealed class JmapAdministrationServiceCollectionExtensionsTests
{
    [Fact]
    public async Task Resolves_every_port_without_user_context_or_backend_credentials()
    {
        await using var host = new StalwartManagementTestHost();
        using var scope = host.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IMailAccounts>().ShouldBeOfType<StalwartMailAccounts>();
        scope.ServiceProvider.GetRequiredService<IMailDomains>().ShouldBeOfType<StalwartMailDomains>();
        scope.ServiceProvider.GetServices<IBackendStatus>().ShouldContain(s => s is StalwartBackendStatus);
        scope.ServiceProvider.GetService<IBackendCredentials>().ShouldBeNull();
        scope.ServiceProvider.GetService<IUserContext>().ShouldBeNull();
    }

    [Fact]
    public void Adds_its_status_next_to_other_backends()
    {
        var services = new ServiceCollection();
        services.AddScoped<IBackendStatus, OtherStatus>();

        services.AddStalwartAdministration(new ConfigurationBuilder().Build());
        services.AddStalwartAdministration(new ConfigurationBuilder().Build());

        services.Where(d => d.ServiceType == typeof(IBackendStatus)).Select(d => d.ImplementationType)
            .ShouldBe([typeof(OtherStatus), typeof(StalwartBackendStatus)]);
    }

    [Fact]
    public void Validates_the_options_at_start()
    {
        var services = new ServiceCollection();
        services.AddStalwartAdministration(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<StalwartManagementOptions>>().Value)
            .Message.ShouldContain("StalwartManagement:ApiUrl is required");
        services.ShouldContain(d => d.ServiceType.Name.Contains("StartupValidator")); // ValidateOnStart
    }

    [Fact]
    public async Task The_management_client_is_cookie_free_and_follows_no_redirects()
    {
        await using var host = new StalwartManagementTestHost();
        var handler = host.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(typeof(IStalwartManagementClient).Name);
        while (handler is DelegatingHandler { InnerHandler: { } inner })
        {
            handler = inner;
        }

        var primary = handler.ShouldBeOfType<SocketsHttpHandler>();
        primary.UseCookies.ShouldBeFalse();
        primary.AllowAutoRedirect.ShouldBeFalse();
    }

    private sealed class OtherStatus : IBackendStatus
    {
        public Task<BackendHealth> CheckAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
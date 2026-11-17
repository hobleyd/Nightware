using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

using Nightware.Application.Administration;

namespace Nightware.Admin.Tests;

/// <summary>The real composition root, with every real adapter: each use case and port resolves.</summary>
public sealed class CompositionTests
{
    [Fact]
    public void Every_use_case_resolves_from_the_real_adapters()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Admin:Authority", "https://kc.example/auth/realms/nightware");
            builder.UseSetting("Admin:ClientSecret", "s");
            builder.UseSetting("Directory:KeycloakBaseUrl", "https://kc.example/auth");
            builder.UseSetting("Directory:Realm", "nightware");
            builder.UseSetting("Directory:ClientId", "nightware-admin");
            builder.UseSetting("Directory:ClientSecret", "s");
            builder.UseSetting("StalwartManagement:ApiUrl", "https://mail.example/jmap/");
            builder.UseSetting("StalwartManagement:Username", "nightware-admin@acme.example");
            builder.UseSetting("StalwartManagement:Password", "s");
            builder.UseSetting("NextcloudProvisioning:BaseUrl", "https://cloud.example/");
            builder.UseSetting("NextcloudProvisioning:Username", "nightware-admin");
            builder.UseSetting("NextcloudProvisioning:Password", "s");
        });
        using IServiceScope scope = factory.Services.CreateScope();

        Type[] useCases = [.. typeof(AdministrationServiceCollectionExtensions).Assembly.GetTypes()
            .Where(t => t.Namespace?.StartsWith("Nightware.Application.Administration.", StringComparison.Ordinal) == true
                && t is { IsClass: true, IsSealed: true, IsPublic: true }
                && t.GetMethod("ExecuteAsync") is not null)];

        useCases.Length.ShouldBeGreaterThan(20);
        foreach (Type useCase in useCases)
        {
            scope.ServiceProvider.GetService(useCase).ShouldNotBeNull(useCase.Name);
        }

        scope.ServiceProvider.GetServices<IBackendStatus>().Count().ShouldBe(3);
    }
}

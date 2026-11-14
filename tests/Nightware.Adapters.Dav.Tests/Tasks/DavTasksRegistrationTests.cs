using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nightware.Adapters.Dav.Tasks;
using Nightware.Adapters.Dav.Tasks.Attachments;
using Nightware.Application.Common.Identity;
using Nightware.Application.Tasks;

namespace Nightware.Adapters.Dav.Tests.Tasks;

public sealed class DavTasksRegistrationTests
{
    [Fact]
    public void Registers_the_task_ports()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => Substitute.For<IBackendCredentials>());
        services.AddDavAdapter(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Dav:BaseUrl"] = "http://nextcloud/" }).Build());
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<ITaskListReader>().ShouldBeOfType<DavTaskListReader>();
        scope.ServiceProvider.GetRequiredService<ITaskStore>().ShouldBeSameAs(scope.ServiceProvider.GetRequiredService<DavTaskStore>());
        scope.ServiceProvider.GetRequiredService<ITaskAttachmentStore>().ShouldBeOfType<DavTaskAttachmentStore>();
    }
}

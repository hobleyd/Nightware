using Microsoft.Extensions.DependencyInjection;
using Nightware.Adapters.Jmap.Mail.Ids;
using Nightware.Adapters.Jmap.Mail.Read;
using Nightware.Adapters.Jmap.Tests.Support;
using Nightware.Application.Mail;

namespace Nightware.Adapters.Jmap.Tests.Mail.Read;

public sealed class JmapMailReadRegistrationTests
{
    [Fact]
    public async Task The_adapter_registers_every_read_port_and_the_locator_per_request()
    {
        await using var host = new JmapTestHost();
        using var scope = host.Services.CreateScope();
        var services = scope.ServiceProvider;

        services.GetRequiredService<IMailFolderReader>().ShouldBeOfType<JmapMailFolderReader>();
        services.GetRequiredService<IMailMessageReader>().ShouldBeOfType<JmapMailMessageReader>();
        services.GetRequiredService<IMailDeltaReader>().ShouldBeOfType<JmapMailDeltaReader>();
        services.GetRequiredService<IRecentCorrespondents>().ShouldBeOfType<JmapRecentCorrespondents>();
        services.GetRequiredService<IJmapMessageLocator>().ShouldBeSameAs(services.GetRequiredService<IJmapMessageLocator>());
    }
}

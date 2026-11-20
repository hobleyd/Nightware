using Microsoft.Extensions.DependencyInjection;
using Nightware.Application.Mail.Read;

namespace Nightware.Application.Tests.Mail.Read;

public sealed class MailReadRegistrationTests
{
    [Fact]
    public void Every_read_use_case_is_registered_per_request()
    {
        var services = new ServiceCollection().AddMailReadUseCases();

        Type[] useCases = [typeof(ListMailFolders), typeof(GetMailFolder), typeof(ListMessages), typeof(GetMessage), typeof(GetMessageMime),
            typeof(ListMessageAttachments), typeof(GetMessageAttachment), typeof(GetMessageAttachmentContent), typeof(SyncMessages), typeof(SyncFolders)];
        services.Select(d => d.ServiceType).ShouldBe(useCases, ignoreOrder: true);
        services.ShouldAllBe(d => d.Lifetime == ServiceLifetime.Scoped);
    }
}

using Microsoft.Extensions.DependencyInjection;

namespace Nightware.Application.Mail.Read;

public static class MailReadServiceCollectionExtensions
{
    /// <summary>Registers the mail read use cases. Owned by Phase 2.</summary>
    public static IServiceCollection AddMailReadUseCases(this IServiceCollection services) => services;
}

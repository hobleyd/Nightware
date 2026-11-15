using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Nightware.Application.MailboxSettings;

public static class MailboxSettingsServiceCollectionExtensions
{
    /// <summary>Registers the MailboxSettings use cases. Owned by the phase that builds MailboxSettings.</summary>
    public static IServiceCollection AddMailboxSettingsUseCases(this IServiceCollection services)
    {
        services.TryAddScoped<GetMailboxSettings>();
        services.TryAddScoped<UpdateMailboxSettings>();
        return services;
    }
}

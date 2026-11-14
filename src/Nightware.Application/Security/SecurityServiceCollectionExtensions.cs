using Microsoft.Extensions.DependencyInjection;

namespace Nightware.Application.Security;

public static class SecurityServiceCollectionExtensions
{
    /// <summary>Registers the Security use cases. Owned by the phase that builds Security.</summary>
    public static IServiceCollection AddSecurityUseCases(this IServiceCollection services) => services;
}

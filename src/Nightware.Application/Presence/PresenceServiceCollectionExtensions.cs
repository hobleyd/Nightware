using Microsoft.Extensions.DependencyInjection;

namespace Nightware.Application.Presence;

public static class PresenceServiceCollectionExtensions
{
    /// <summary>Registers the Presence use cases (phase 25, ADR 0009): presence over Nextcloud user status.</summary>
    public static IServiceCollection AddPresenceUseCases(this IServiceCollection services) => services;
}

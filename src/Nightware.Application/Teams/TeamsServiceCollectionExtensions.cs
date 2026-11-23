using Microsoft.Extensions.DependencyInjection;

namespace Nightware.Application.Teams;

public static class TeamsServiceCollectionExtensions
{
    /// <summary>Registers the Teams use cases (phase 24, ADR 0009): teams and channels over Nextcloud Teams (circles) and Talk.</summary>
    public static IServiceCollection AddTeamsUseCases(this IServiceCollection services) => services;
}

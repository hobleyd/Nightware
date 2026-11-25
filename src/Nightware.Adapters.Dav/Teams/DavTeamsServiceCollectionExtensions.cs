using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Nightware.Application.Teams;

namespace Nightware.Adapters.Dav.Teams;

internal static class DavTeamsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Teams ports (phase 24, ADR 0009): teams and channels over Nextcloud Teams (circles) and Talk. They
    /// need only the OCS API (the <c>Dav</c> section) and reuse the Talk pieces <c>AddDavChats</c> registers.
    /// </summary>
    public static IServiceCollection AddDavTeams(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddScoped<NextcloudCircles>();
        services.TryAddScoped<TalkChannelStore>();
        services.TryAddScoped<ITeamStore, NextcloudTeamStore>();
        services.TryAddScoped<ITeamMembers, NextcloudTeamMembers>();
        services.TryAddScoped<IChannelStore>(static sp => sp.GetRequiredService<TalkChannelStore>());
        services.TryAddScoped<IChannelMessages, TalkChannelMessages>();
        services.TryAddScoped<Application.Subscriptions.ITeamsChangeFeed, Chats.TalkChangeFeed>();
        return services;
    }
}

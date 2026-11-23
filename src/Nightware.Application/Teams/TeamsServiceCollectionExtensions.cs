using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Nightware.Application.Teams;

public static class TeamsServiceCollectionExtensions
{
    /// <summary>Registers the Teams use cases (phase 24, ADR 0009): teams and channels over Nextcloud Teams (circles) and Talk.</summary>
    public static IServiceCollection AddTeamsUseCases(this IServiceCollection services)
    {
        services.TryAddScoped<ListJoinedTeams>();
        services.TryAddScoped<GetTeam>();
        services.TryAddScoped<CreateTeam>();
        services.TryAddScoped<GetTeamOperation>();
        services.TryAddScoped<UpdateTeam>();
        services.TryAddScoped<DeleteTeam>();
        services.TryAddScoped<ListTeamMembers>();
        services.TryAddScoped<GetTeamMember>();
        services.TryAddScoped<AddTeamMember>();
        services.TryAddScoped<UpdateTeamMember>();
        services.TryAddScoped<RemoveTeamMember>();
        services.TryAddScoped<ListChannels>();
        services.TryAddScoped<GetChannel>();
        services.TryAddScoped<GetPrimaryChannel>();
        services.TryAddScoped<CreateChannel>();
        services.TryAddScoped<UpdateChannel>();
        services.TryAddScoped<DeleteChannel>();
        services.TryAddScoped<ListChannelMembers>();
        services.TryAddScoped<GetChannelFilesFolder>();
        services.TryAddScoped<ListChannelMessages>();
        services.TryAddScoped<ListChannelMessageReplies>();
        services.TryAddScoped<GetChannelMessage>();
        services.TryAddScoped<SendChannelMessage>();
        services.TryAddScoped<SendChannelMessageReply>();
        services.TryAddScoped<UpdateChannelMessage>();
        services.TryAddScoped<SoftDeleteChannelMessage>();
        services.TryAddScoped<SetChannelMessageReaction>();
        services.TryAddScoped<UnsetChannelMessageReaction>();
        services.TryAddScoped<GetChannelMessagesDelta>();
        return services;
    }
}

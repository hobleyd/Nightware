using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Nightware.Application.OnlineMeetings;

public static class OnlineMeetingsServiceCollectionExtensions
{
    /// <summary>Registers the OnlineMeetings use cases (phase 26, ADR 0009): online meetings over Talk rooms, with their recordings and transcripts.</summary>
    public static IServiceCollection AddOnlineMeetingsUseCases(this IServiceCollection services)
    {
        services.TryAddScoped<CreateOnlineMeeting>();
        services.TryAddScoped<GetOnlineMeeting>();
        services.TryAddScoped<FindOnlineMeetingByJoinUrl>();
        services.TryAddScoped<UpdateOnlineMeeting>();
        services.TryAddScoped<DeleteOnlineMeeting>();
        services.TryAddScoped<ListMeetingArtifacts>();
        services.TryAddScoped<GetMeetingArtifact>();
        services.TryAddScoped<DownloadMeetingArtifact>();
        return services;
    }
}

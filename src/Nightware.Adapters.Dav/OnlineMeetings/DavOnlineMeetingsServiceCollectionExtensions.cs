using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using Nightware.Adapters.Dav.Talk;
using Nightware.Application.OnlineMeetings;

namespace Nightware.Adapters.Dav.OnlineMeetings;

internal static class DavOnlineMeetingsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the OnlineMeetings ports (phase 26, ADR 0009): online meetings over Talk rooms, with their recordings and
    /// transcripts. Without <c>Talk:PublicBaseUrl</c> every operation is 501.
    /// </summary>
    public static IServiceCollection AddDavOnlineMeetings(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddTalkOptions(configuration);
        services.AddTalkPeople();
        services.TryAddScoped<NextcloudOnlineMeetings>();
        services.TryAddScoped<TalkMeetingArtifacts>();
        services.TryAddScoped<IOnlineMeetingStore>(static sp =>
            Configured(sp) ? sp.GetRequiredService<NextcloudOnlineMeetings>() : UnconfiguredOnlineMeetingStore.Instance);
        services.TryAddScoped<IMeetingArtifacts>(static sp =>
            Configured(sp) ? sp.GetRequiredService<TalkMeetingArtifacts>() : UnconfiguredOnlineMeetingStore.Instance);
        return services;
    }

    private static bool Configured(IServiceProvider services) => services.GetRequiredService<IOptions<TalkOptions>>().Value.PublicBaseUrl is not null;
}

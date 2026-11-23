using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Nightware.Adapters.Dav.Talk;
using Nightware.Application.Calendar.OnlineMeetings;

namespace Nightware.Adapters.Dav.Calendar.Talk;

internal static class DavTalkServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="IOnlineMeetings"/>: Nextcloud Talk when <c>Talk:PublicBaseUrl</c> is set,
    /// otherwise <see cref="UnconfiguredOnlineMeetings"/> (events are saved without a meeting room).
    /// </summary>
    public static IServiceCollection AddDavTalk(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddTalkOptions(configuration);
        services.TryAddScoped<NextcloudTalkMeetings>();
        services.TryAddScoped<IOnlineMeetings>(static sp =>
            sp.GetRequiredService<IOptions<TalkOptions>>().Value.PublicBaseUrl is null
                ? new UnconfiguredOnlineMeetings()
                : sp.GetRequiredService<NextcloudTalkMeetings>());
        return services;
    }
}

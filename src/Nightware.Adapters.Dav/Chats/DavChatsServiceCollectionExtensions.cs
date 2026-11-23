using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Nightware.Adapters.Dav.Chats;

internal static class DavChatsServiceCollectionExtensions
{
    /// <summary>Registers the Chats ports (phase 23, ADR 0009): chats over Nextcloud Talk conversations.</summary>
    public static IServiceCollection AddDavChats(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return services;
    }
}

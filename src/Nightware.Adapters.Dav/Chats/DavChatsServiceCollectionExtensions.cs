using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Nightware.Adapters.Dav.Chats.Talk;
using Nightware.Application.Chats;
using Nightware.Application.Directory;

namespace Nightware.Adapters.Dav.Chats;

internal static class DavChatsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Chats ports (phase 23, ADR 0009): chats over Nextcloud Talk conversations. They need only Talk's
    /// OCS API (the <c>Dav</c> section); <c>Talk:PublicBaseUrl</c>, when set, adds web links. The <c>Talk/</c> pieces
    /// (client, codec, feed, identities) are registered here for channels (phase 24) to reuse.
    /// </summary>
    public static IServiceCollection AddDavChats(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        services.TryAddScoped<TalkChatClient>();
        // The directory map comes from the Directory adapter, composed after this one; without it (an adapter on its
        // own, as in tests) people keep Talk's names and no ids.
        services.TryAddScoped(static sp => new TalkIdentities(sp.GetService<IUserAccountNames>() ?? TalkIdentities.NoDirectory));
        services.TryAddScoped<TalkMessageFeed>();
        services.TryAddScoped<TalkChannelRooms>();
        services.TryAddSingleton<TalkLinks>();
        services.TryAddScoped<TalkChatRooms>();
        services.TryAddScoped<IChatStore, TalkChatStore>();
        services.TryAddScoped<IChatMembers, TalkChatMembers>();
        services.TryAddScoped<IChatMessages, TalkChatMessages>();
        services.TryAddScoped<IChatPins, TalkChatPins>();
        return services;
    }
}

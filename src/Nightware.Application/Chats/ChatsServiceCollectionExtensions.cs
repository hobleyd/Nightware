using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Nightware.Application.Chats;

public static class ChatsServiceCollectionExtensions
{
    /// <summary>Registers the Chats use cases (phase 23, ADR 0009): chats over Nextcloud Talk conversations.</summary>
    public static IServiceCollection AddChatsUseCases(this IServiceCollection services)
    {
        services.TryAddScoped<ListChats>();
        services.TryAddScoped<GetChat>();
        services.TryAddScoped<CreateChat>();
        services.TryAddScoped<UpdateChat>();
        services.TryAddScoped<HideChat>();
        services.TryAddScoped<UnhideChat>();
        services.TryAddScoped<MarkChatRead>();
        services.TryAddScoped<MarkChatUnread>();
        services.TryAddScoped<ListChatMembers>();
        services.TryAddScoped<GetChatMember>();
        services.TryAddScoped<AddChatMember>();
        services.TryAddScoped<RemoveChatMember>();
        services.TryAddScoped<ListChatMessages>();
        services.TryAddScoped<GetChatMessage>();
        services.TryAddScoped<SendChatMessage>();
        services.TryAddScoped<UpdateChatMessage>();
        services.TryAddScoped<SoftDeleteChatMessage>();
        services.TryAddScoped<SetChatMessageReaction>();
        services.TryAddScoped<UnsetChatMessageReaction>();
        services.TryAddScoped<ListPinnedChatMessages>();
        services.TryAddScoped<PinChatMessage>();
        services.TryAddScoped<UnpinChatMessage>();
        services.TryAddScoped<SearchChatMessages>();
        return services;
    }
}

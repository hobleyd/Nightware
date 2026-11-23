using Microsoft.Extensions.DependencyInjection;

namespace Nightware.Application.Chats;

public static class ChatsServiceCollectionExtensions
{
    /// <summary>Registers the Chats use cases (phase 23, ADR 0009): chats over Nextcloud Talk conversations.</summary>
    public static IServiceCollection AddChatsUseCases(this IServiceCollection services) => services;
}

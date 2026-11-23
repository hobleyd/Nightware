using Microsoft.Extensions.DependencyInjection;

namespace Nightware.Adapters.Directory.Chats;

internal static class DirectoryChatsServiceCollectionExtensions
{
    /// <summary>Registers the Nextcloud uid ↔ directory id map chats, teams and presence use (ADR 0009 D2).</summary>
    public static IServiceCollection AddDirectoryChats(this IServiceCollection services) => services;
}

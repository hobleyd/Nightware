using Microsoft.AspNetCore.Routing;

namespace Nightware.Api.Features.Chats;

/// <summary>Chats over Nextcloud Talk conversations (phase 23, ADR 0009).</summary>
public static class ChatsEndpoints
{
    public static RouteGroupBuilder MapChatsEndpoints(this RouteGroupBuilder graph) => graph;
}

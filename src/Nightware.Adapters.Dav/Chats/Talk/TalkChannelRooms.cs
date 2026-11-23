using System.Text.Json;

using Nightware.Adapters.Dav.Core;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Chats.Talk;

/// <summary>
/// The tokens of the caller's channel rooms: the Talk resources of the teams (circles) they are in
/// (<c>apps/circles/circles</c>, then <c>teams/{id}/resources</c>, ADR 0009 D5). Talk's room list can't tell a channel
/// from a group chat, and Teams leaves channels out of chats (D3).
/// </summary>
internal sealed class TalkChannelRooms(IDavClient dav)
{
    internal const string CirclesPath = "apps/circles/circles";
    internal const string TalkProvider = "talk";

    /// <summary>
    /// Every channel room token. Teams being unavailable (the app disabled, a team gone in between) means no channels
    /// rather than no chats: the worst case is a channel listed as a group chat.
    /// </summary>
    public async Task<IReadOnlySet<string>> TokensAsync(CancellationToken cancellationToken)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var circles = await dav.OcsGetAsync(CirclesPath, null, cancellationToken).ConfigureAwait(false);
        if (!circles.IsSuccess || circles.Value.ValueKind != JsonValueKind.Array)
        {
            return tokens;
        }

        foreach (var circle in circles.Value.EnumerateArray())
        {
            if (TalkJson.String(circle, "id") is not { Length: > 0 } id || !id.All(char.IsAsciiLetterOrDigit))
            {
                continue;
            }

            var resources = await dav.OcsGetAsync($"teams/{id}/resources", null, cancellationToken).ConfigureAwait(false);
            foreach (var token in TalkTokens(resources))
            {
                tokens.Add(token);
            }
        }

        return tokens;
    }

    private static IEnumerable<string> TalkTokens(Result<JsonElement> resources)
    {
        if (!resources.IsSuccess || resources.Value.ValueKind != JsonValueKind.Object
            || !resources.Value.TryGetProperty("resources", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var resource in list.EnumerateArray())
        {
            if (resource.TryGetProperty("provider", out var provider) && TalkJson.String(provider, "id") == TalkProvider
                && TalkJson.String(resource, "id") is { } token && TalkIds.IsToken(token))
            {
                yield return token;
            }
        }
    }
}

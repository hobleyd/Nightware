using Microsoft.Extensions.Options;

using Nightware.Adapters.Dav.Talk;

namespace Nightware.Adapters.Dav.Chats.Talk;

/// <summary>
/// Web links into Talk: a conversation is <c>{Talk:PublicBaseUrl}call/{token}</c>, the page Talk's own resources and
/// phase 22's join links use. <c>Dav:BaseUrl</c> is usually an internal address, so without <c>Talk:PublicBaseUrl</c>
/// there are no links (chats still work; <c>webUrl</c> is left out).
/// </summary>
internal sealed class TalkLinks(IOptions<TalkOptions> options)
{
    public string? ConversationUrl(string token)
    {
        if (options.Value.PublicBaseUrl is not { } root)
        {
            return null;
        }

        var text = root.AbsoluteUri;
        return (text.EndsWith('/') ? text : text + "/") + "call/" + token;
    }
}

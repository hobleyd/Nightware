using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Nightware.Api.Graph.Routing;
using Nightware.Application.Chats;
using Nightware.Application.Common.Identity;
using Nightware.Application.Common.Query;
using Nightware.Domain.Chats;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Features.Chats;

/// <summary>Reads what chat requests carry: list options, chat creations, members, message drafts and Graph's <c>@odata.bind</c> links.</summary>
internal static partial class ChatRequests
{
    public const string UserBind = "user@odata.bind";
    public const string MessageBind = "message@odata.bind";

    /// <summary>A <c>/users/{x}/chats…</c> route is the caller's own chats only: the façade reads Talk as the caller.</summary>
    public static Error? OwnChats(Mailbox mailbox, IUserContext caller)
    {
        ArgumentNullException.ThrowIfNull(mailbox);
        ArgumentNullException.ThrowIfNull(caller);
        return mailbox.IsMe || string.Equals(mailbox.Ref.Address, caller.Principal.Mail, StringComparison.OrdinalIgnoreCase)
            ? null
            : Error.Forbidden("Only your own chats can be reached.");
    }

    /// <summary>
    /// A listing's page: <c>$top</c> (up to 50) and <c>$skiptoken</c>; <c>$expand</c> only of <paramref name="expandable"/>.
    /// <c>$filter</c>, <c>$search</c>, <c>$skip</c> and <c>$count</c> aren't offered, nor any order but Graph's own.
    /// </summary>
    public static Result<ChatPageQuery> PageQuery(ItemQuery query, params string[] expandable)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Filter is not null || query.Search is not null || query.Skip is not null || query.Count)
        {
            return Error.Validation("This listing takes $select, $expand, $top, $orderby and $skiptoken only.");
        }

        if (query.OrderBy.Any(o => o.Direction != SortDirection.Descending
                                   || !(o.Property.Equals("createdDateTime", StringComparison.OrdinalIgnoreCase)
                                        || o.Property.Equals("lastModifiedDateTime", StringComparison.OrdinalIgnoreCase)
                                        || o.Property.Equals("lastMessagePreview/createdDateTime", StringComparison.OrdinalIgnoreCase))))
        {
            return Error.Validation("This listing is newest first; $orderby can only say so.", "$orderby");
        }

        if (query.Expand.FirstOrDefault(e => !expandable.Contains(e.NavigationProperty, StringComparer.OrdinalIgnoreCase)) is { } unsupported)
        {
            return Error.Validation($"'{unsupported.NavigationProperty}' can't be expanded here.", "$expand");
        }

        var top = ChatRules.PageSize(query.Top);
        return top.IsSuccess
            ? new ChatPageQuery(top.Value, query.SkipToken) { WithLastMessage = query.ExpandOf("lastMessagePreview") is not null }
            : top.Error;
    }

    /// <summary>A <c>POST /chats</c> body: <c>chatType</c>, <c>topic</c> and <c>members</c>.</summary>
    public static Result<ChatCreation> Creation(JsonObject? body)
    {
        if (body is null)
        {
            return Error.Validation("A chat needs a body.");
        }

        var type = Text(body, "chatType")?.ToLowerInvariant() switch
        {
            "oneonone" => ChatType.OneOnOne,
            "group" => ChatType.Group,
            "meeting" => ChatType.Meeting,
            _ => (ChatType?)null,
        };
        if (type is null)
        {
            return Error.Validation("chatType must be 'oneOnOne' or 'group'.", "chatType");
        }

        if (body["members"] is not JsonArray members || members.Count == 0)
        {
            return Error.Validation("A chat needs its members.", "members");
        }

        var drafts = new List<ChatMemberDraft>();
        foreach (var node in members)
        {
            var member = Member(node as JsonObject);
            if (!member.IsSuccess)
            {
                return member.Error;
            }

            drafts.Add(member.Value);
        }

        return new ChatCreation(type.Value, Text(body, "topic"), drafts);
    }

    /// <summary>A conversation member: <c>user@odata.bind</c> (or <c>userId</c>) and <c>roles</c>.</summary>
    public static Result<ChatMemberDraft> Member(JsonObject? body)
    {
        if (body is null)
        {
            return Error.Validation("A member needs a body.", "members");
        }

        if (Text(body, "@odata.type") is { } type
            && !type.TrimStart('#').Equals("microsoft.graph.aadUserConversationMember", StringComparison.OrdinalIgnoreCase))
        {
            return Error.Validation("Only aadUserConversationMember members can be added.", "@odata.type");
        }

        var user = UserOfBind(Text(body, UserBind)) ?? Text(body, "userId");
        if (string.IsNullOrWhiteSpace(user))
        {
            return Error.Validation($"A member needs '{UserBind}'.", UserBind);
        }

        var roles = body["roles"] is JsonArray array
            ? array.Select(r => r is JsonValue v && v.TryGetValue<string>(out var role) ? role : "").ToList()
            : [];
        if (roles.Any(r => !r.Equals(ChatMember.OwnerRole, StringComparison.OrdinalIgnoreCase) && !r.Equals("guest", StringComparison.OrdinalIgnoreCase)))
        {
            return Error.Validation("A member's roles are [] or [\"owner\"].", "roles");
        }

        return new ChatMemberDraft(user, roles.Any(r => ChatMember.OwnerRole.Equals(r, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The user in Graph's <c>…/users('{id}')</c> or <c>…/users/{id}</c>; null for anything else.</summary>
    public static string? UserOfBind(string? bind)
    {
        if (string.IsNullOrWhiteSpace(bind))
        {
            return null;
        }

        var match = UsersBind().Match(bind);
        return match.Success ? Uri.UnescapeDataString(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value) : null;
    }

    /// <summary>The message id in <c>message@odata.bind</c> (<c>…/chats/{chatId}/messages/{id}</c>), which must name this chat.</summary>
    public static Result<string> MessageOfBind(JsonObject? body, string chatId)
    {
        var bind = Text(body, MessageBind);
        var match = bind is null ? Match.Empty : MessagesBind().Match(bind);
        if (!match.Success)
        {
            return Error.Validation($"'{MessageBind}' must name a message: …/chats/{{chatId}}/messages/{{messageId}}.", MessageBind);
        }

        var chat = Uri.UnescapeDataString(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value);
        return string.Equals(chat, chatId, StringComparison.Ordinal)
            ? Uri.UnescapeDataString(match.Groups[3].Success ? match.Groups[3].Value : match.Groups[4].Value)
            : Error.Validation($"'{MessageBind}' names a message of another chat.", MessageBind);
    }

    /// <summary>A message body to send or set: <c>body</c>, <c>mentions</c> and <c>subject</c> (importance has nowhere to go).</summary>
    public static Result<ChatMessageDraft> Draft(JsonObject? body)
    {
        if (body?["body"] is not JsonObject content)
        {
            return Error.Validation("A message needs a body.", "body");
        }

        var contentType = Text(content, "contentType")?.ToLowerInvariant() switch
        {
            null or "text" => ChatBodyType.Text,
            "html" => ChatBodyType.Html,
            _ => (ChatBodyType?)null,
        };
        if (contentType is null)
        {
            return Error.Validation("body.contentType must be 'text' or 'html'.", "body.contentType");
        }

        var mentions = new List<ChatMentionDraft>();
        if (body["mentions"] is JsonArray array)
        {
            foreach (var node in array)
            {
                if (node is not JsonObject mention || mention["id"] is not JsonValue idValue || !idValue.TryGetValue<int>(out var id))
                {
                    return Error.Validation("Each mention needs a numeric id.", "mentions");
                }

                var mentioned = mention["mentioned"] as JsonObject;
                var conversation = mentioned?["conversation"] is JsonObject;
                var user = mentioned?["user"] is JsonObject u ? Text(u, "id") : null;
                mentions.Add(new ChatMentionDraft(id, Text(mention, "mentionText"), user, conversation && user is null));
            }
        }

        return new ChatMessageDraft(new ChatMessageBody(contentType.Value, Text(content, "content") ?? string.Empty))
        {
            Mentions = mentions,
            Subject = Text(body, "subject"),
        };
    }

    /// <summary><c>user.id</c> of a <c>…ForUser</c> action's body.</summary>
    public static string? UserId(JsonObject? body) => body?["user"] is JsonObject user ? Text(user, "id") : null;

    public static string? Text(JsonObject? body, string name) =>
        body is not null && body.TryGetPropertyValue(name, out var node) && node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    [GeneratedRegex(@"users\('([^']+)'\)|users/([^/?#()']+)/?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex UsersBind();

    [GeneratedRegex(@"chats(?:\('([^']+)'\)|/([^/?#()']+))/messages(?:\('([^']+)'\)|/([^/?#()']+))/?$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex MessagesBind();
}

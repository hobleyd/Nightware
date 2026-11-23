using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nightware.Adapters.Dav.Talk;

namespace Nightware.Adapters.Dav.Chats.Talk;

/// <summary>Talk's room types (<c>Room::TYPE_*</c>).</summary>
internal static class TalkRoomType
{
    public const int OneToOne = 1;
    public const int Group = 2;
    public const int Public = 3;
    public const int Changelog = 4;
    public const int FormerOneToOne = 5;
    public const int NoteToSelf = 6;
}

/// <summary>Talk's participant types (<c>Participant::*</c>).</summary>
internal static class TalkParticipantType
{
    public const int Owner = 1;
    public const int Moderator = 2;
    public const int User = 3;
    public const int Guest = 4;
    public const int UserSelfJoined = 5;
    public const int GuestModerator = 6;

    public static bool Moderates(int type) => type is Owner or Moderator or GuestModerator;
}

/// <summary>
/// Talk's ids, checked before any goes into an OCS path: a room token is Talk's own route requirement, a message id is
/// a positive decimal, a membership id is <c>m.</c> plus the attendee id (ADR 0009 D2). Anything else is "not found".
/// </summary>
internal static partial class TalkIds
{
    public const string MembershipPrefix = "m.";

    public static bool IsToken(string? token) => token is not null && TokenPattern().IsMatch(token);

    public static bool TryMessageId(string? id, out long messageId)
    {
        messageId = 0;
        return id is { Length: > 0 and <= 18 } && id.All(char.IsAsciiDigit)
               && long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out messageId) && messageId > 0;
    }

    public static string MembershipId(long attendeeId) => MembershipPrefix + attendeeId.ToString(CultureInfo.InvariantCulture);

    public static bool TryAttendeeId(string? membershipId, out long attendeeId)
    {
        attendeeId = 0;
        return membershipId is not null && membershipId.StartsWith(MembershipPrefix, StringComparison.Ordinal)
               && TryMessageId(membershipId[MembershipPrefix.Length..], out attendeeId);
    }

    public static string Decimal(long id) => id.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex("^[a-z0-9]{4,30}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}

/// <summary>Someone Talk names: an actor (<c>actorType</c>/<c>actorId</c>) or a rich-object parameter of a person.</summary>
/// <param name="Type">Talk's actor type: <c>users</c>, <c>guests</c>, <c>emails</c>, <c>federated_users</c>, <c>bots</c>, ….</param>
internal sealed record TalkActor(string Type, string Id, string? DisplayName)
{
    public const string Users = "users";
    public const string Guests = "guests";
    public const string Emails = "emails";
    public const string FederatedUsers = "federated_users";
    public const string System = "system";

    public bool IsUser => Type == Users;

    /// <summary>A person named by a message parameter (<c>type</c> <c>user</c>, <c>guest</c>, …), as an actor.</summary>
    public static TalkActor? FromParameter(TalkParameter? parameter) => parameter?.Type switch
    {
        "user" when parameter.Id is { Length: > 0 } id => new TalkActor(Users, id, parameter.Name),
        // Talk's own events name it as the guest "guest/system".
        "guest" when parameter.Id == "guest/system" => new TalkActor(System, parameter.Id, parameter.Name),
        "guest" or "email" when parameter.Id is { Length: > 0 } id => new TalkActor(Guests, id, parameter.Name),
        "user" or "guest" or "email" => null,
        { } other when parameter.Id is { Length: > 0 } id => new TalkActor(other, id, parameter.Name),
        _ => null,
    };
}

/// <summary>A rich-object message parameter (<c>messageParameters.{key}</c>): a mention, a file, the actor, ….</summary>
internal sealed record TalkParameter(string Type, string? Id, string? Name)
{
    /// <summary>Every string property of the parameter (<c>link</c>, <c>mention-id</c>, <c>mimetype</c>, …).</summary>
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();

    public string? this[string name] => Properties.TryGetValue(name, out var value) ? value : null;

    public static TalkParameter Parse(JsonElement element)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                {
                    properties[property.Name] = property.Value.GetString()!;
                }
                else if (property.Value.ValueKind == JsonValueKind.Number)
                {
                    properties[property.Name] = property.Value.GetRawText();
                }
            }
        }

        return new TalkParameter(
            properties.GetValueOrDefault("type") ?? string.Empty,
            properties.GetValueOrDefault("id"),
            properties.GetValueOrDefault("name"))
        {
            Properties = properties,
        };
    }
}

/// <summary>One person's reaction (<c>GET reaction/{token}/{messageId}</c>).</summary>
internal sealed record TalkReaction(string Emoji, TalkActor Actor, DateTimeOffset At);

/// <summary>A Talk chat message (<c>ChatMessage</c> in Talk's OpenAPI document), as the wire carries it.</summary>
internal sealed record TalkMessage(long Id, string Token, TalkActor Actor, DateTimeOffset Timestamp)
{
    public const string Comment = "comment";
    public const string CommentDeleted = "comment_deleted";
    public const string System = "system";

    public string Text { get; init; } = string.Empty;

    /// <summary>Keyed as the placeholders in <see cref="Text"/> (<c>{mention-user1}</c> → <c>mention-user1</c>).</summary>
    public IReadOnlyDictionary<string, TalkParameter> Parameters { get; init; } = new Dictionary<string, TalkParameter>();

    /// <summary><c>comment</c>, <c>comment_deleted</c>, <c>system</c>, <c>command</c>, <c>voice-message</c>, ….</summary>
    public string MessageType { get; init; } = Comment;

    /// <summary>The system message kind (<c>user_added</c>, …); empty for a comment.</summary>
    public string SystemMessage { get; init; } = string.Empty;

    public bool IsMarkdown { get; init; }

    /// <summary>Emoji → count.</summary>
    public IReadOnlyDictionary<string, int> ReactionCounts { get; init; } = new Dictionary<string, int>();

    /// <summary>Who reacted, when read separately (<c>GET reaction/…</c>); null when not read.</summary>
    public IReadOnlyList<TalkReaction>? Reactions { get; init; }

    /// <summary>The thread the message is in; its own id when it isn't in one.</summary>
    public long ThreadId { get; init; }

    /// <summary>The message starts a thread (a channel post, ADR 0009 D5).</summary>
    public bool IsThread { get; init; }

    public string? ThreadTitle { get; init; }

    /// <summary>The message this one replies to or, for a system message, the one it is about.</summary>
    public TalkMessage? Parent { get; init; }

    public DateTimeOffset? LastEditedAt { get; init; }

    /// <summary>Who pinned it and when (only in the pinned listing).</summary>
    public DateTimeOffset? PinnedAt { get; init; }

    public bool IsDeleted => MessageType == CommentDeleted;

    public bool IsSystem => MessageType == System;

    public static TalkMessage Parse(JsonElement element, bool withParent = true)
    {
        var id = TalkJson.Long(element, "id") ?? 0;
        var parameters = new Dictionary<string, TalkParameter>(StringComparer.Ordinal);
        // PHP writes an empty map as [], so an object is only an object when it has members.
        if (element.TryGetProperty("messageParameters", out var p) && p.ValueKind == JsonValueKind.Object)
        {
            foreach (var parameter in p.EnumerateObject())
            {
                parameters[parameter.Name] = TalkParameter.Parse(parameter.Value);
            }
        }

        var reactions = new Dictionary<string, int>(StringComparer.Ordinal);
        if (element.TryGetProperty("reactions", out var r) && r.ValueKind == JsonValueKind.Object)
        {
            foreach (var reaction in r.EnumerateObject())
            {
                if (reaction.Value.TryGetInt32(out var count) && count > 0)
                {
                    reactions[reaction.Name] = count;
                }
            }
        }

        var metaData = element.TryGetProperty("metaData", out var m) && m.ValueKind == JsonValueKind.Object ? m : default;
        return new TalkMessage(
            id,
            TalkJson.String(element, "token") ?? string.Empty,
            new TalkActor(TalkJson.String(element, "actorType") ?? string.Empty, TalkJson.String(element, "actorId") ?? string.Empty, TalkJson.String(element, "actorDisplayName")),
            TalkJson.Instant(TalkJson.Long(element, "timestamp") ?? 0))
        {
            Text = TalkJson.String(element, "message") ?? string.Empty,
            Parameters = parameters,
            MessageType = TalkJson.String(element, "messageType") ?? Comment,
            SystemMessage = TalkJson.String(element, "systemMessage") ?? string.Empty,
            IsMarkdown = element.TryGetProperty("markdown", out var md) && md.ValueKind == JsonValueKind.True,
            ReactionCounts = reactions,
            ThreadId = TalkJson.Long(element, "threadId") ?? id,
            IsThread = element.TryGetProperty("isThread", out var t) && t.ValueKind == JsonValueKind.True,
            ThreadTitle = TalkJson.String(element, "threadTitle"),
            Parent = withParent && element.TryGetProperty("parent", out var parent) && parent.ValueKind == JsonValueKind.Object
                ? Parse(parent, withParent: false)
                : null,
            LastEditedAt = TalkJson.Long(element, "lastEditTimestamp") is > 0 and var edited ? TalkJson.Instant(edited) : null,
            PinnedAt = metaData.ValueKind == JsonValueKind.Object && TalkJson.Long(metaData, "pinnedAt") is > 0 and var pinned ? TalkJson.Instant(pinned) : null,
        };
    }

    /// <summary>A list of messages, or the values of a map keyed by message id (the share listing's shape); [] for none.</summary>
    public static IReadOnlyList<TalkMessage> ParseMany(JsonElement data) => data.ValueKind switch
    {
        JsonValueKind.Array => data.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).Select(e => Parse(e)).ToList(),
        JsonValueKind.Object => data.EnumerateObject().Where(e => e.Value.ValueKind == JsonValueKind.Object).Select(e => Parse(e.Value)).ToList(),
        _ => [],
    };
}

/// <summary>A Talk conversation (<c>Room</c> in Talk's OpenAPI document), as seen by the caller.</summary>
internal sealed record TalkRoom(long Id, string Token, int Type)
{
    public string Name { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string ObjectType { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    /// <summary>The caller's own attendee id in the room.</summary>
    public long AttendeeId { get; init; }

    /// <summary>The caller's participant type.</summary>
    public int ParticipantType { get; init; }

    public bool IsArchived { get; init; }

    public DateTimeOffset? LastActivity { get; init; }

    public long LastReadMessage { get; init; }

    public int UnreadMessages { get; init; }

    public TalkMessage? LastMessage { get; init; }

    public bool IsOneToOne => Type is TalkRoomType.OneToOne or TalkRoomType.FormerOneToOne;

    public static TalkRoom Parse(JsonElement element) => new(
        TalkJson.Long(element, "id") ?? 0,
        TalkJson.String(element, "token") ?? string.Empty,
        (int)(TalkJson.Long(element, "type") ?? 0))
    {
        Name = TalkJson.String(element, "name") ?? string.Empty,
        DisplayName = TalkJson.String(element, "displayName") ?? string.Empty,
        ObjectType = TalkJson.String(element, "objectType") ?? string.Empty,
        Description = TalkJson.String(element, "description") ?? string.Empty,
        AttendeeId = TalkJson.Long(element, "attendeeId") ?? 0,
        ParticipantType = (int)(TalkJson.Long(element, "participantType") ?? 0),
        IsArchived = element.TryGetProperty("isArchived", out var archived) && archived.ValueKind == JsonValueKind.True,
        LastActivity = TalkJson.Long(element, "lastActivity") is > 0 and var activity ? TalkJson.Instant(activity) : null,
        LastReadMessage = TalkJson.Long(element, "lastReadMessage") ?? 0,
        UnreadMessages = (int)(TalkJson.Long(element, "unreadMessages") ?? 0),
        LastMessage = element.TryGetProperty("lastMessage", out var last) && last.ValueKind == JsonValueKind.Object && TalkJson.Long(last, "id") is > 0
            ? TalkMessage.Parse(last)
            : null,
    };
}

/// <summary>A room's participant (<c>Participant</c> in Talk's OpenAPI document).</summary>
internal sealed record TalkParticipant(long AttendeeId, TalkActor Actor, int ParticipantType)
{
    public static TalkParticipant Parse(JsonElement element) => new(
        TalkJson.Long(element, "attendeeId") ?? 0,
        new TalkActor(TalkJson.String(element, "actorType") ?? string.Empty, TalkJson.String(element, "actorId") ?? string.Empty, TalkJson.String(element, "displayName")),
        (int)(TalkJson.Long(element, "participantType") ?? 0));
}

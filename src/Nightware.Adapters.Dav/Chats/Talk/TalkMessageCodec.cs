using System.Globalization;
using System.Text.RegularExpressions;

using Nightware.Domain.Chats;

namespace Nightware.Adapters.Dav.Chats.Talk;

/// <summary>
/// Where messages are read from: a chat (<see cref="ChatId"/> on each message) or a team's channel
/// (<see cref="Channel"/>), with the conversation's name (for <c>@all</c> mentions) and its web link.
/// </summary>
internal sealed record TalkConversation(string Token, string? DisplayName)
{
    /// <summary>Set for a channel's room (phase 24): messages then carry <c>channelIdentity</c>, not <c>chatId</c>.</summary>
    public ChannelIdentity? Channel { get; init; }

    /// <summary>The conversation's page in Talk (<c>{Talk:PublicBaseUrl}call/{token}</c>); null when Talk's public URL isn't configured.</summary>
    public string? WebUrl { get; init; }

    public bool IsChannel => Channel is not null;
}

/// <summary>
/// Talk chat messages ↔ Graph <c>chatMessage</c> (ADR 0009 D4), for chats (phase 23) and channels (phase 24). Pure and
/// synchronous: the caller resolves Talk's people to directory identities first (<see cref="AccountsIn"/>, then a
/// lookup) and hands the result in as <c>identify</c>.
/// </summary>
internal static partial class TalkMessageCodec
{
    public const string UserAdded = "user_added";
    public const string UserRemoved = "user_removed";
    public const string ConversationRenamed = "conversation_renamed";
    public const string CallEnded = "call_ended";
    public const string CallEndedEveryone = "call_ended_everyone";
    public const string MessageDeleted = "message_deleted";

    /// <summary>System messages about another message (their parent), which a delta or a change feed re-reports.</summary>
    public static readonly IReadOnlySet<string> ChangesToParent = new HashSet<string>(StringComparer.Ordinal)
    {
        "message_edited", MessageDeleted, "thread_renamed", "reaction", "reaction_revoked", "reaction_deleted",
    };

    private static readonly HashSet<string> ReportedSystemMessages = new(StringComparer.Ordinal)
    {
        UserAdded, UserRemoved, ConversationRenamed, CallEnded, CallEndedEveryone,
    };

    /// <summary>
    /// Whether Graph lists the message: comments and deleted comments, and the system events Teams has a counterpart
    /// for. Every other system message (reactions, edits, deletes, pins, thread creation, …) is Talk's bookkeeping.
    /// </summary>
    public static bool IsListed(TalkMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.IsSystem ? ReportedSystemMessages.Contains(message.SystemMessage) : message.Id > 0;
    }

    /// <summary>The longest title <see cref="DefaultThreadTitle"/> gives.</summary>
    public const int DefaultTitleLength = 100;

    /// <summary>A message in a thread other than its first: a channel reply (ADR 0009 D5).</summary>
    public static bool IsThreadReply(TalkMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.IsThread && message.ThreadId > 0 && message.ThreadId != message.Id;
    }

    /// <summary>
    /// A channel post: a thread's first message, or a top-level message sent outside threads (Talk's own clients), which
    /// is a post without replies.
    /// </summary>
    public static bool IsPost(TalkMessage message) => !IsThreadReply(message);

    /// <summary>
    /// The title a post gets when Graph's request has no subject (Talk needs one to start a thread): the body's first
    /// line with text, at most <see cref="DefaultTitleLength"/> characters, and <see cref="UntitledThread"/> when there is
    /// none. Mentions are left out (<c>@"uid"</c> and <c>@all</c> as sent, <c>{mention-…}</c> as Talk keeps them), so
    /// the title taken from a sent text and from the stored one are the same. Such a post reports no subject.
    /// </summary>
    public static string DefaultThreadTitle(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var line = MentionsInText().Replace(text, " ").Split('\n')
            .Select(l => WhiteSpace().Replace(l, " ").Trim())
            .FirstOrDefault(l => l.Length > 0) ?? UntitledThread;
        return line.Length > DefaultTitleLength ? line[..DefaultTitleLength].TrimEnd() : line;
    }

    /// <summary>The title of a post whose body has no line of text besides mentions.</summary>
    public const string UntitledThread = "Post";

    /// <summary>A post's subject: its thread's title, unless that is only the default taken from the body, or the post is deleted.</summary>
    private static string? SubjectOf(TalkMessage message) =>
        message.ThreadTitle is { Length: > 0 } title && !message.IsDeleted && title != DefaultThreadTitle(message.Text) ? title : null;

    /// <summary>The Talk user ids (Nextcloud uids) a set of messages names, to resolve before decoding.</summary>
    public static IEnumerable<string> AccountsIn(IEnumerable<TalkMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        foreach (var message in messages)
        {
            if (message.Actor.IsUser)
            {
                yield return message.Actor.Id;
            }

            foreach (var parameter in message.Parameters.Values)
            {
                if (TalkActor.FromParameter(parameter) is { IsUser: true } person)
                {
                    yield return person.Id;
                }
            }

            foreach (var reaction in message.Reactions ?? [])
            {
                if (reaction.Actor.IsUser)
                {
                    yield return reaction.Actor.Id;
                }
            }
        }
    }

    /// <summary>
    /// The Graph message for a Talk message, or null for one Graph doesn't list (<see cref="IsListed"/>).
    /// </summary>
    /// <param name="identify">A Talk person as a directory identity (ADR 0009 D2).</param>
    /// <param name="deletedAt">When a deleted message was deleted, where the caller saw Talk's <c>message_deleted</c> event for it.</param>
    public static ChatMessage? ToChatMessage(TalkMessage message, TalkConversation conversation, Func<TalkActor, ChatIdentity> identify, DateTimeOffset? deletedAt = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(identify);
        if (!IsListed(message))
        {
            return null;
        }

        var id = TalkIds.Decimal(message.Id);
        var chatMessage = new ChatMessage(id, ChatMessageType.Message, message.Timestamp)
        {
            ChatId = conversation.IsChannel ? null : conversation.Token,
            ChannelIdentity = conversation.Channel,
            WebUrl = conversation.WebUrl is { } url ? $"{url}#message_{id}" : null,

            // In a channel a reply names its post (the thread's first message, ADR 0009 D5); a chat has no replies. Only a
            // message in a thread is a reply: Talk's replyTo outside threads also gives the parent's id as threadId.
            ReplyToId = conversation.IsChannel && IsThreadReply(message) ? TalkIds.Decimal(message.ThreadId) : null,
            Subject = conversation.IsChannel && message.IsThread && !IsThreadReply(message) ? SubjectOf(message) : null,
        };

        if (message.IsSystem)
        {
            return chatMessage with
            {
                MessageType = ChatMessageType.SystemEventMessage,
                LastModifiedDateTime = message.Timestamp,
                Body = ChatMessageBody.SystemEvent,
                EventDetail = EventDetail(message, conversation, identify),
            };
        }

        if (message.IsDeleted)
        {
            var deleted = deletedAt ?? message.LastEditedAt ?? message.Timestamp;
            return chatMessage with
            {
                From = identify(message.Actor),
                DeletedDateTime = deleted,
                LastModifiedDateTime = Latest(message.Timestamp, deleted),
                Body = ChatMessageBody.Deleted,
            };
        }

        var mentions = new List<ChatMention>();
        var mentionIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var attachments = new List<ChatMessageAttachment>();
        string? Placeholder(string key)
        {
            if (!message.Parameters.TryGetValue(key, out var parameter))
            {
                return null;
            }

            switch (parameter.Type)
            {
                case "user" or "guest" or "email" or "federated_user" when TalkActor.FromParameter(parameter) is { } person:
                {
                    var identity = identify(person);
                    var text = identity.DisplayName ?? parameter.Name ?? person.Id;
                    return MentionTag(key, text, new ChatMentioned.User(identity));
                }

                case "call":
                {
                    var text = parameter.Name ?? conversation.DisplayName ?? "Everyone";
                    return MentionTag(key, text, new ChatMentioned.Conversation(conversation.IsChannel ? conversation.Channel!.ChannelId : conversation.Token, conversation.DisplayName ?? parameter.Name, conversation.IsChannel ? "channel" : "chat"));
                }

                case "file" when parameter.Id is { Length: > 0 } fileId:
                    attachments.Add(new ChatMessageAttachment(fileId, ChatMessageAttachment.Reference, parameter["link"], parameter.Name));
                    return $"<attachment id=\"{TalkMarkdown.Encode(fileId)}\"></attachment>";

                default:
                    return parameter.Name is { } name ? TalkMarkdown.Encode(name) : null;
            }
        }

        string MentionTag(string key, string text, ChatMentioned mentioned)
        {
            if (!mentionIds.TryGetValue(key, out var mentionId))
            {
                mentionId = mentions.Count;
                mentionIds[key] = mentionId;
                mentions.Add(new ChatMention(mentionId, text, mentioned));
            }

            return $"<at id=\"{mentionId.ToString(CultureInfo.InvariantCulture)}\">{TalkMarkdown.Encode(text)}</at>";
        }

        var html = message.IsMarkdown ? TalkMarkdown.ToHtml(message.Text, Placeholder) : TalkMarkdown.PlainToHtml(message.Text, Placeholder);
        return chatMessage with
        {
            From = identify(message.Actor),
            LastEditedDateTime = message.LastEditedAt,
            LastModifiedDateTime = message.LastEditedAt is { } edited ? Latest(message.Timestamp, edited) : message.Timestamp,
            Body = new ChatMessageBody(ChatBodyType.Html, html),
            Mentions = mentions,
            Attachments = attachments,
            Reactions = (message.Reactions ?? [])
                .Select(r => new ChatReaction(r.Emoji, r.At, identify(r.Actor)))
                .OrderBy(r => r.CreatedDateTime)
                .ToList(),
        };
    }

    /// <summary>
    /// When each deleted message in a batch was deleted: Talk's <c>message_deleted</c> event names its message as parent.
    /// </summary>
    public static IReadOnlyDictionary<long, DateTimeOffset> DeletionTimes(IEnumerable<TalkMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var times = new Dictionary<long, DateTimeOffset>();
        foreach (var message in messages)
        {
            if (message.IsSystem && message.SystemMessage == MessageDeleted && message.Parent is { } parent)
            {
                times.TryAdd(parent.Id, message.Timestamp);
            }
        }

        return times;
    }

    /// <summary>
    /// A Graph body as the Markdown Talk sends: a text body as it is, an html body converted (ADR 0009 D4), with each
    /// <c>&lt;at id="n"&gt;</c> becoming <paramref name="mentions"/>' Markdown for that id (Talk's <c>@"uid"</c> or <c>@all</c>).
    /// </summary>
    public static string ToTalkText(ChatMessageBody body, IReadOnlyDictionary<int, string> mentions)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(mentions);
        return body.ContentType == ChatBodyType.Text
            ? body.Content
            : TalkHtml.ToMarkdown(body.Content, id => mentions.TryGetValue(id, out var markdown) ? markdown : null);
    }

    /// <summary>Talk's mention of a user (<c>@"uid"</c>; quoted, since a uid may hold spaces).</summary>
    public static string MentionOf(string accountName)
    {
        ArgumentException.ThrowIfNullOrEmpty(accountName);
        return "@\"" + accountName.Replace("\"", string.Empty, StringComparison.Ordinal) + "\"";
    }

    /// <summary>Talk's mention of everyone in the conversation.</summary>
    public const string MentionAll = "@all";

    private static ChatEventDetail? EventDetail(TalkMessage message, TalkConversation conversation, Func<TalkActor, ChatIdentity> identify)
    {
        var initiator = TalkActor.FromParameter(message.Parameters.GetValueOrDefault("actor")) is { } actor ? identify(actor) : null;
        switch (message.SystemMessage)
        {
            case UserAdded:
                return new ChatEventDetail.MembersAdded(initiator, People(message, "user", identify));
            case UserRemoved:
                return new ChatEventDetail.MembersDeleted(initiator, People(message, "user", identify));
            case ConversationRenamed:
                var renamed = RenamedTo().Match(message.Text);
                return new ChatEventDetail.ChatRenamed(initiator, conversation.IsChannel ? conversation.Channel!.ChannelId : conversation.Token,
                    renamed.Success ? renamed.Groups[1].Value : null);
            default:
                // call_ended(_everyone): the participants are user1…userN; the duration is only in the rendered text.
                var participants = message.Parameters
                    .Where(p => p.Key.StartsWith("user", StringComparison.Ordinal) || p.Key.StartsWith("guest", StringComparison.Ordinal))
                    .OrderBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => TalkActor.FromParameter(p.Value))
                    .OfType<TalkActor>()
                    .Select(identify)
                    .ToList();
                return new ChatEventDetail.CallEnded(initiator, Duration(message.Text), participants);
        }
    }

    private static List<ChatIdentity> People(TalkMessage message, string key, Func<TalkActor, ChatIdentity> identify) =>
        TalkActor.FromParameter(message.Parameters.GetValueOrDefault(key)) is { } person ? [identify(person)] : [];

    /// <summary>The call's length from Talk's "(Duration 1:02:03)" or "(Duration 0:14)"; null when the text doesn't say.</summary>
    internal static TimeSpan? Duration(string text)
    {
        var match = DurationPattern().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var parts = match.Groups[1].Value.Split(':').Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
        return parts.Length == 3
            ? new TimeSpan(parts[0], parts[1], parts[2])
            : new TimeSpan(0, parts[0], parts[1]);
    }

    private static DateTimeOffset Latest(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;

    /// <summary>Talk's "… renamed the conversation from "old" to "new"".</summary>
    [GeneratedRegex("\"[^\"]*\" to \"(.*)\"\\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex RenamedTo();

    /// <summary>Mentions in Talk's text: <c>@"uid"</c>, <c>@all</c> or <c>@uid</c> as sent, <c>{mention-…}</c> placeholders as stored.</summary>
    [GeneratedRegex(@"@""[^""]*""|@[A-Za-z0-9_.\-]+|\{mention-[a-z]+\d*\}", RegexOptions.CultureInvariant)]
    private static partial Regex MentionsInText();

    [GeneratedRegex(@"[ \t\r]+", RegexOptions.CultureInvariant)]
    private static partial Regex WhiteSpace();

    [GeneratedRegex(@"\((?:[^()]*\s)?(\d{1,4}(?::\d{2}){1,2})\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex DurationPattern();
}

/// <summary>Graph identities for Talk people when the directory has nothing to add.</summary>
internal static class TalkIdentityDefaults
{
    /// <summary>The identity Talk alone can give: no directory id, Talk's display name, the kind its actor type implies.</summary>
    public static ChatIdentity Anonymous(TalkActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return new ChatIdentity(null, actor.DisplayName, KindOf(actor));
    }

    public static ChatIdentityKind KindOf(TalkActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        return actor.Type switch
        {
            TalkActor.Users or TalkActor.FederatedUsers or "deleted_users" => ChatIdentityKind.User,
            TalkActor.Guests or TalkActor.Emails => ChatIdentityKind.Guest,
            _ => ChatIdentityKind.Application,
        };
    }
}

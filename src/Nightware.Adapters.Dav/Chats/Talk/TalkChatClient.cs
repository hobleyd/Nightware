using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Nightware.Adapters.Dav.Core;
using Nightware.Domain.Kernel;
using Nightware.Adapters.Dav.Talk;

namespace Nightware.Adapters.Dav.Chats.Talk;

/// <summary>One page of Talk's chat history, newest first, and the id to read on from (<c>X-Chat-Last-Given</c>).</summary>
/// <param name="LastGiven">Null when the history has nothing older (Talk's 304).</param>
internal sealed record TalkHistoryPage(IReadOnlyList<TalkMessage> Messages, long? LastGiven);

/// <summary>A message to post: Markdown, optionally replying, in a thread, or starting one (a channel post).</summary>
internal sealed record TalkPost(string Message)
{
    public long? ReplyTo { get; init; }

    public long? ThreadId { get; init; }

    public string? ThreadTitle { get; init; }
}

/// <summary>
/// Talk's OCS API as the caller (<c>apps/spreed/api/v4/room</c>, <c>api/v1/chat</c>, <c>api/v1/reaction</c>), for any
/// conversation: chats (phase 23) and channel rooms (phase 24). Every token and id is checked before it goes into a
/// path; a malformed one is <see cref="ErrorKind.NotFound"/>, as Talk would answer. Reads never move the read marker,
/// clear notifications or mark the caller online (<c>setReadMarker=0</c>, <c>markNotificationsAsRead=0</c>,
/// <c>noStatusUpdate=1</c>): a Graph read is not someone looking at Talk.
/// </summary>
internal sealed class TalkChatClient(IDavClient dav)
{
    internal const string RoomsPath = "apps/spreed/api/v4/room";
    internal const string ChatPath = "apps/spreed/api/v1/chat";
    internal const string ReactionPath = "apps/spreed/api/v1/reaction";
    internal const string SearchPath = "search/providers/talk-message/search";

    /// <summary>Talk's largest history page.</summary>
    internal const int MaxHistoryPage = 200;

    private static readonly Dictionary<string, string> QuietRead = new(StringComparer.Ordinal) { ["noStatusUpdate"] = "1" };

    public async Task<Result<IReadOnlyList<TalkRoom>>> ListRoomsAsync(CancellationToken cancellationToken)
    {
        var listed = await dav.OcsGetAsync(RoomsPath, QuietRead, cancellationToken).ConfigureAwait(false);
        if (!listed.IsSuccess)
        {
            return listed.Error;
        }

        return listed.Value.ValueKind == JsonValueKind.Array
            ? listed.Value.EnumerateArray().Select(TalkRoom.Parse).Where(r => TalkIds.IsToken(r.Token)).ToList()
            : Unexpected("room list");
    }

    public async Task<Result<TalkRoom>> GetRoomAsync(string token, CancellationToken cancellationToken)
    {
        if (!TalkIds.IsToken(token))
        {
            return NoConversation();
        }

        var room = await dav.OcsGetAsync($"{RoomsPath}/{token}", QuietRead, cancellationToken).ConfigureAwait(false);
        return room.IsSuccess ? Room(room.Value) : NotFoundAs(room.Error, NoConversation());
    }

    /// <summary>Creates a room; for a one-to-one room Talk returns the existing one when there is one.</summary>
    public async Task<Result<TalkRoom>> CreateRoomAsync(int roomType, string? name, IReadOnlyList<string> users, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(users);
        var body = new JsonObject
        {
            ["roomType"] = roomType,
            ["participants"] = new JsonObject { ["users"] = new JsonArray(users.Select(u => (JsonNode)u).ToArray()) },
        };
        if (name is not null)
        {
            body["roomName"] = name;
        }

        var created = await dav.OcsExchangeAsync(new OcsRequest(HttpMethod.Post, RoomsPath) { Json = body }, cancellationToken).ConfigureAwait(false);
        return created.IsSuccess ? Room(created.Value.Data) : created.Error;
    }

    /// <summary>
    /// Creates a team's channel room (phase 24): a group room whose participants include the team, so Talk adds and
    /// removes people as the team changes (ADR 0009 D5).
    /// </summary>
    public async Task<Result<TalkRoom>> CreateTeamRoomAsync(string name, string? description, string teamId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(teamId);
        var body = new JsonObject
        {
            ["roomType"] = TalkRoomType.Group,
            ["roomName"] = name,
            ["participants"] = new JsonObject { ["teams"] = new JsonArray(teamId) },
        };
        if (!string.IsNullOrEmpty(description))
        {
            body["description"] = description;
        }

        var created = await dav.OcsExchangeAsync(new OcsRequest(HttpMethod.Post, RoomsPath) { Json = body }, cancellationToken).ConfigureAwait(false);
        return created.IsSuccess ? Room(created.Value.Data) : created.Error;
    }

    /// <summary>Deletes the room for everyone (a moderator's action).</summary>
    public Task<Result> DeleteRoomAsync(string token, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Delete, token, $"{RoomsPath}/{token}", null, cancellationToken);

    public Task<Result> SetDescriptionAsync(string token, string description, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Put, token, $"{RoomsPath}/{token}/description",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["description"] = description }, cancellationToken);

    /// <summary>Renames a thread (Talk answers with the thread; a <c>thread_renamed</c> event goes into the chat).</summary>
    public Task<Result> RenameThreadAsync(string token, long threadId, string title, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Put, token, $"{ChatPath}/{token}/threads/{TalkIds.Decimal(threadId)}",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["threadTitle"] = title }, cancellationToken, NoMessage());

    public Task<Result> RenameRoomAsync(string token, string name, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Put, token, $"{RoomsPath}/{token}", new Dictionary<string, string>(StringComparer.Ordinal) { ["roomName"] = name }, cancellationToken);

    public Task<Result> SetArchivedAsync(string token, bool archived, CancellationToken cancellationToken) =>
        SendAsync(archived ? HttpMethod.Post : HttpMethod.Delete, token, $"{RoomsPath}/{token}/archive", null, cancellationToken);

    /// <summary>Moves the caller's read marker to the newest message (Talk's <c>chat-read-last</c>).</summary>
    public Task<Result> MarkReadAsync(string token, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, token, $"{ChatPath}/{token}/read", null, cancellationToken);

    public Task<Result> MarkUnreadAsync(string token, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Delete, token, $"{ChatPath}/{token}/read", null, cancellationToken);

    public async Task<Result<IReadOnlyList<TalkParticipant>>> ParticipantsAsync(string token, CancellationToken cancellationToken)
    {
        if (!TalkIds.IsToken(token))
        {
            return NoConversation();
        }

        var listed = await dav.OcsGetAsync($"{RoomsPath}/{token}/participants", null, cancellationToken).ConfigureAwait(false);
        if (!listed.IsSuccess)
        {
            return NotFoundAs(listed.Error, NoConversation());
        }

        return listed.Value.ValueKind == JsonValueKind.Array
            ? listed.Value.EnumerateArray().Select(TalkParticipant.Parse).ToList()
            : Unexpected("participant list");
    }

    public Task<Result> AddUserAsync(string token, string accountName, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, token, $"{RoomsPath}/{token}/participants",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["newParticipant"] = accountName, ["source"] = TalkActor.Users }, cancellationToken);

    public Task<Result> PromoteAsync(string token, long attendeeId, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, token, $"{RoomsPath}/{token}/moderators",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["attendeeId"] = TalkIds.Decimal(attendeeId) }, cancellationToken);

    public async Task<Result> RemoveAttendeeAsync(string token, long attendeeId, CancellationToken cancellationToken)
    {
        if (!TalkIds.IsToken(token))
        {
            return NoConversation();
        }

        var removed = await dav.OcsExchangeAsync(new OcsRequest(HttpMethod.Delete, $"{RoomsPath}/{token}/attendees")
        {
            Query = new Dictionary<string, string>(StringComparer.Ordinal) { ["attendeeId"] = TalkIds.Decimal(attendeeId) },
        }, cancellationToken).ConfigureAwait(false);
        return removed.IsSuccess ? Result.Success() : removed.Error;
    }

    /// <summary>The caller leaves the room.</summary>
    public Task<Result> LeaveAsync(string token, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Delete, token, $"{RoomsPath}/{token}/participants/self", null, cancellationToken);

    /// <summary>
    /// Messages older than <paramref name="before"/> (the newest when null), newest first, Talk's bookkeeping included;
    /// <paramref name="threadId"/> reads one thread.
    /// </summary>
    public async Task<Result<TalkHistoryPage>> HistoryAsync(string token, long? before, int limit, long? threadId, CancellationToken cancellationToken)
    {
        if (!TalkIds.IsToken(token))
        {
            return NoConversation();
        }

        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["lookIntoFuture"] = "0",
            ["limit"] = Math.Clamp(limit, 1, MaxHistoryPage).ToString(CultureInfo.InvariantCulture),
            ["setReadMarker"] = "0",
            ["markNotificationsAsRead"] = "0",
            ["noStatusUpdate"] = "1",
        };
        if (before is { } known)
        {
            query["lastKnownMessageId"] = TalkIds.Decimal(known);
        }

        if (threadId is { } thread)
        {
            query["threadId"] = TalkIds.Decimal(thread);
        }

        var read = await dav.OcsExchangeAsync(OcsRequest.Get($"{ChatPath}/{token}", query), cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            return NotFoundAs(read.Error, NoConversation());
        }

        var messages = TalkMessage.ParseMany(read.Value.Data);
        var lastGiven = long.TryParse(read.Value.Header("X-Chat-Last-Given"), NumberStyles.None, CultureInfo.InvariantCulture, out var given) && given > 0
            ? given
            : messages.Count > 0 ? messages.Min(m => m.Id) : (long?)null;
        return new TalkHistoryPage(messages, messages.Count == 0 ? null : lastGiven);
    }

    /// <summary>
    /// Messages newer than <paramref name="after"/>, oldest first, without waiting for more (<c>lookIntoFuture=1</c>,
    /// <c>timeout=0</c>); <see cref="TalkHistoryPage.LastGiven"/> is the id to read on from, null when nothing is newer.
    /// </summary>
    public async Task<Result<TalkHistoryPage>> FutureAsync(string token, long after, int limit, CancellationToken cancellationToken)
    {
        if (!TalkIds.IsToken(token))
        {
            return NoConversation();
        }

        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["lookIntoFuture"] = "1",
            ["timeout"] = "0",
            ["lastKnownMessageId"] = TalkIds.Decimal(Math.Max(0, after)),
            ["limit"] = Math.Clamp(limit, 1, MaxHistoryPage).ToString(CultureInfo.InvariantCulture),
            ["setReadMarker"] = "0",
            ["markNotificationsAsRead"] = "0",
            ["noStatusUpdate"] = "1",
        };
        var read = await dav.OcsExchangeAsync(OcsRequest.Get($"{ChatPath}/{token}", query), cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            return NotFoundAs(read.Error, NoConversation());
        }

        var messages = TalkMessage.ParseMany(read.Value.Data).OrderBy(m => m.Id).ToList();
        var lastGiven = long.TryParse(read.Value.Header("X-Chat-Last-Given"), NumberStyles.None, CultureInfo.InvariantCulture, out var given) && given > 0
            ? given
            : messages.Count > 0 ? messages.Max(m => m.Id) : (long?)null;
        return new TalkHistoryPage(messages, messages.Count == 0 ? null : lastGiven);
    }

    /// <summary>One message by id, or <see cref="ErrorKind.NotFound"/>. Talk's context call answers the nearest message for an id it lacks, so the id is checked.</summary>
    public async Task<Result<TalkMessage>> MessageAsync(string token, long messageId, CancellationToken cancellationToken)
    {
        if (!TalkIds.IsToken(token))
        {
            return NoConversation();
        }

        var read = await dav.OcsExchangeAsync(OcsRequest.Get($"{ChatPath}/{token}/{TalkIds.Decimal(messageId)}/context",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["limit"] = "0" }), cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            return NotFoundAs(read.Error, NoMessage());
        }

        return TalkMessage.ParseMany(read.Value.Data).FirstOrDefault(m => m.Id == messageId) is { } message ? message : NoMessage();
    }

    public async Task<Result<TalkMessage>> PostAsync(string token, TalkPost post, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(post);
        if (!TalkIds.IsToken(token))
        {
            return NoConversation();
        }

        var form = new Dictionary<string, string>(StringComparer.Ordinal) { ["message"] = post.Message };
        if (post.ReplyTo is { } replyTo)
        {
            form["replyTo"] = TalkIds.Decimal(replyTo);
        }

        if (post.ThreadId is { } threadId)
        {
            form["threadId"] = TalkIds.Decimal(threadId);
        }

        if (!string.IsNullOrEmpty(post.ThreadTitle))
        {
            form["threadTitle"] = post.ThreadTitle;
        }

        var sent = await dav.OcsSendAsync(HttpMethod.Post, $"{ChatPath}/{token}", form, cancellationToken).ConfigureAwait(false);
        if (!sent.IsSuccess)
        {
            return NotFoundAs(sent.Error, NoConversation());
        }

        return sent.Value.ValueKind == JsonValueKind.Object ? TalkMessage.Parse(sent.Value) : Unexpected("sent message");
    }

    /// <summary>Replaces a message's text; Talk answers with its <c>message_edited</c> event, whose parent is the edited message.</summary>
    public async Task<Result<TalkMessage>> EditAsync(string token, long messageId, string message, CancellationToken cancellationToken)
    {
        if (!TalkIds.IsToken(token))
        {
            return NoConversation();
        }

        var edited = await dav.OcsSendAsync(HttpMethod.Put, $"{ChatPath}/{token}/{TalkIds.Decimal(messageId)}",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["message"] = message }, cancellationToken).ConfigureAwait(false);
        if (!edited.IsSuccess)
        {
            return NotFoundAs(edited.Error, NoMessage());
        }

        return edited.Value.ValueKind == JsonValueKind.Object && TalkMessage.Parse(edited.Value).Parent is { } parent ? parent : Unexpected("edited message");
    }

    public Task<Result> DeleteMessageAsync(string token, long messageId, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Delete, token, $"{ChatPath}/{token}/{TalkIds.Decimal(messageId)}", null, cancellationToken, NoMessage());

    public Task<Result> ReactAsync(string token, long messageId, string emoji, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, token, $"{ReactionPath}/{token}/{TalkIds.Decimal(messageId)}",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["reaction"] = emoji }, cancellationToken, NoMessage());

    public async Task<Result> UnreactAsync(string token, long messageId, string emoji, CancellationToken cancellationToken)
    {
        if (!TalkIds.IsToken(token))
        {
            return NoConversation();
        }

        var removed = await dav.OcsExchangeAsync(new OcsRequest(HttpMethod.Delete, $"{ReactionPath}/{token}/{TalkIds.Decimal(messageId)}")
        {
            Query = new Dictionary<string, string>(StringComparer.Ordinal) { ["reaction"] = emoji },
        }, cancellationToken).ConfigureAwait(false);
        return removed.IsSuccess ? Result.Success() : NotFoundAs(removed.Error, NoMessage());
    }

    /// <summary>Who reacted to a message, with what and when.</summary>
    public async Task<Result<IReadOnlyList<TalkReaction>>> ReactionsAsync(string token, long messageId, CancellationToken cancellationToken)
    {
        if (!TalkIds.IsToken(token))
        {
            return NoConversation();
        }

        var read = await dav.OcsGetAsync($"{ReactionPath}/{token}/{TalkIds.Decimal(messageId)}", null, cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            return NotFoundAs(read.Error, NoMessage());
        }

        var reactions = new List<TalkReaction>();
        if (read.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (var emoji in read.Value.EnumerateObject())
            {
                if (emoji.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var who in emoji.Value.EnumerateArray())
                {
                    reactions.Add(new TalkReaction(emoji.Name,
                        new TalkActor(TalkJson.String(who, "actorType") ?? string.Empty, TalkJson.String(who, "actorId") ?? string.Empty, TalkJson.String(who, "actorDisplayName")),
                        TalkJson.Instant(TalkJson.Long(who, "timestamp") ?? 0)));
                }
            }
        }

        return reactions;
    }

    /// <summary>The room's pinned messages (Talk's shared-items listing of type <c>pinned</c>), most recently pinned first.</summary>
    public async Task<Result<IReadOnlyList<TalkMessage>>> PinnedAsync(string token, CancellationToken cancellationToken)
    {
        if (!TalkIds.IsToken(token))
        {
            return NoConversation();
        }

        var read = await dav.OcsExchangeAsync(OcsRequest.Get($"{ChatPath}/{token}/share",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["objectType"] = "pinned", ["limit"] = TalkIds.Decimal(MaxHistoryPage) }), cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            return NotFoundAs(read.Error, NoConversation());
        }

        return TalkMessage.ParseMany(read.Value.Data).OrderByDescending(m => m.PinnedAt).ThenByDescending(m => m.Id).ToList();
    }

    public Task<Result> PinAsync(string token, long messageId, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Post, token, $"{ChatPath}/{token}/{TalkIds.Decimal(messageId)}/pin", null, cancellationToken, NoMessage());

    public Task<Result> UnpinAsync(string token, long messageId, CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Delete, token, $"{ChatPath}/{token}/{TalkIds.Decimal(messageId)}/pin", null, cancellationToken, NoMessage());

    /// <summary>
    /// Talk's message search (the unified search provider <c>talk-message</c>, <see cref="SearchPath"/>), newest first:
    /// a window of <paramref name="limit"/> matches from <paramref name="offset"/> over every conversation of the
    /// caller's. Nextcloud caps a page (25 by default).
    /// </summary>
    public async Task<Result<TalkSearchPage>> SearchMessagesAsync(string term, int offset, int limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(term);
        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["term"] = term,
            ["limit"] = limit.ToString(CultureInfo.InvariantCulture),
            ["cursor"] = offset.ToString(CultureInfo.InvariantCulture),
        };
        var read = await dav.OcsGetAsync(SearchPath, query, cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            return read.Error;
        }

        return read.Value.ValueKind == JsonValueKind.Object && read.Value.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array
            ? TalkSearchPage.Parse(entries)
            : Unexpected("search result");
    }

    internal static Error NoConversation() => Error.NotFound("The chat was not found, or you are not a member of it.");

    internal static Error NoMessage() => Error.NotFound("The message was not found in this chat.");

    private async Task<Result> SendAsync(HttpMethod method, string token, string path, IReadOnlyDictionary<string, string>? form, CancellationToken cancellationToken, Error? notFound = null)
    {
        if (!TalkIds.IsToken(token))
        {
            return NoConversation();
        }

        var sent = await dav.OcsSendAsync(method, path, form, cancellationToken).ConfigureAwait(false);
        return sent.IsSuccess ? Result.Success() : NotFoundAs(sent.Error, notFound ?? NoConversation());
    }

    private static Result<TalkRoom> Room(JsonElement data) =>
        data.ValueKind == JsonValueKind.Object && TalkRoom.Parse(data) is { } room && TalkIds.IsToken(room.Token) ? room : Unexpected("room");

    /// <summary>A Talk 404 with Graph's wording; every other error as it is (Talk's reason is already in its message).</summary>
    private static Error NotFoundAs(Error error, Error notFound) => error.Kind == ErrorKind.NotFound ? notFound : error;

    private static Error Unexpected(string what) => Error.BackendUnavailable($"Nextcloud Talk returned an unexpected {what}.");
}

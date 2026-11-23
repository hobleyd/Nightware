using Nightware.Application.Chats;
using Nightware.Domain.Chats;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Teams;

/// <summary>A post with its replies, when <c>$expand=replies</c> asked for them.</summary>
public sealed record ChannelPostView(ChatMessage Post, IReadOnlyList<ChatMessage>? Replies = null);

/// <summary>Checks on channel posts.</summary>
public static class ChannelMessageRules
{
    /// <summary>The longest subject a post may have: Talk cuts a longer thread title to 200 characters and an ellipsis.</summary>
    public const int MaxSubjectLength = 200;

    /// <summary>How many replies <c>$expand=replies</c> gives each post (Graph's own expansion is bounded too).</summary>
    public const int ExpandedReplies = 50;

    public static Error? Subject(string? subject) =>
        subject is not null && (string.IsNullOrWhiteSpace(subject) || subject.Length > MaxSubjectLength)
            ? Error.Validation($"A subject has 1 to {MaxSubjectLength} characters.", "subject")
            : null;
}

/// <summary>Graph <c>GET /teams/{id}/channels/{channelId}/messages</c> (and <c>$expand=replies</c>).</summary>
public sealed class ListChannelMessages(IChannelMessages messages)
{
    public async Task<Result<Page<ChannelPostView>>> ExecuteAsync(
        string teamId, string channelId, ChatPageQuery query, bool withReplies, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var top = ChatRules.PageSize(query.Top);
        if (!top.IsSuccess)
        {
            return top.Error;
        }

        var page = await messages.ListPostsAsync(teamId, channelId, query, cancellationToken).ConfigureAwait(false);
        if (!page.IsSuccess)
        {
            return page.Error;
        }

        var views = new List<ChannelPostView>(page.Value.Items.Count);
        foreach (var post in page.Value.Items)
        {
            if (!withReplies)
            {
                views.Add(new ChannelPostView(post));
                continue;
            }

            var replies = await messages.ListRepliesAsync(new ChannelMessageRef(teamId, channelId, post.Id), new ChatPageQuery(ChannelMessageRules.ExpandedReplies, null), cancellationToken)
                .ConfigureAwait(false);
            if (!replies.IsSuccess)
            {
                return replies.Error;
            }

            views.Add(new ChannelPostView(post, replies.Value.Items));
        }

        return new Page<ChannelPostView>(views, page.Value.SkipToken);
    }
}

/// <summary>Graph <c>GET /teams/{id}/channels/{channelId}/messages/{messageId}/replies</c>.</summary>
public sealed class ListChannelMessageReplies(IChannelMessages messages)
{
    public async Task<Result<Page<ChatMessage>>> ExecuteAsync(ChannelMessageRef post, ChatPageQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(query);
        var top = ChatRules.PageSize(query.Top);
        return top.IsSuccess ? await messages.ListRepliesAsync(post, query, cancellationToken).ConfigureAwait(false) : top.Error;
    }
}

/// <summary>Graph <c>GET …/messages/{messageId}</c> and <c>…/replies/{replyId}</c>.</summary>
public sealed class GetChannelMessage(IChannelMessages messages)
{
    public Task<Result<ChatMessage>> ExecuteAsync(ChannelMessageRef message, CancellationToken cancellationToken) =>
        messages.GetAsync(message, cancellationToken);
}

/// <summary>Graph <c>POST /teams/{id}/channels/{channelId}/messages</c>: a new post.</summary>
public sealed class SendChannelMessage(IChannelMessages messages)
{
    public async Task<Result<ChatMessage>> ExecuteAsync(string teamId, string channelId, ChatMessageDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return (ChatMessageRules.Validate(draft) ?? ChannelMessageRules.Subject(draft.Subject)) is { } invalid
            ? invalid
            : await messages.PostAsync(teamId, channelId, draft, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Graph <c>POST …/messages/{messageId}/replies</c>. A reply has no subject of its own.</summary>
public sealed class SendChannelMessageReply(IChannelMessages messages)
{
    public async Task<Result<ChatMessage>> ExecuteAsync(ChannelMessageRef post, ChatMessageDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(draft);
        return ChatMessageRules.Validate(draft) is { } invalid
            ? invalid
            : await messages.ReplyAsync(post, draft with { Subject = null }, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Graph <c>PATCH …/messages/{messageId}</c> and <c>…/replies/{replyId}</c>: the sender changes the body (and a post's subject).</summary>
public sealed class UpdateChannelMessage(IChannelMessages messages)
{
    public async Task<Result> ExecuteAsync(ChannelMessageRef message, ChatMessageDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(draft);
        if ((ChatMessageRules.Validate(draft) ?? ChannelMessageRules.Subject(draft.Subject)) is { } invalid)
        {
            return invalid;
        }

        if (message.IsReply && draft.Subject is not null)
        {
            return Error.Validation("A reply has no subject.", "subject");
        }

        return await messages.UpdateAsync(message, draft, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Graph <c>POST …/softDelete</c> on a post or a reply.</summary>
public sealed class SoftDeleteChannelMessage(IChannelMessages messages)
{
    public Task<Result> ExecuteAsync(ChannelMessageRef message, CancellationToken cancellationToken) =>
        messages.SoftDeleteAsync(message, cancellationToken);
}

/// <summary>Graph <c>POST …/setReaction</c> on a post or a reply.</summary>
public sealed class SetChannelMessageReaction(IChannelMessages messages)
{
    public async Task<Result> ExecuteAsync(ChannelMessageRef message, string? reactionType, CancellationToken cancellationToken)
    {
        var reaction = ChatMessageRules.Reaction(reactionType);
        return reaction.IsSuccess
            ? await messages.SetReactionAsync(message, reaction.Value, cancellationToken).ConfigureAwait(false)
            : reaction.Error;
    }
}

/// <summary>Graph <c>POST …/unsetReaction</c> on a post or a reply.</summary>
public sealed class UnsetChannelMessageReaction(IChannelMessages messages)
{
    public async Task<Result> ExecuteAsync(ChannelMessageRef message, string? reactionType, CancellationToken cancellationToken)
    {
        var reaction = ChatMessageRules.Reaction(reactionType);
        return reaction.IsSuccess
            ? await messages.UnsetReactionAsync(message, reaction.Value, cancellationToken).ConfigureAwait(false)
            : reaction.Error;
    }
}

/// <summary>Graph <c>GET /teams/{id}/channels/{channelId}/messages/delta</c>: new and changed posts.</summary>
public sealed class GetChannelMessagesDelta(IChannelMessages messages)
{
    public Task<Result<DeltaPage<ChatMessage>>> ExecuteAsync(string teamId, string channelId, DeltaToken? token, int? top, CancellationToken cancellationToken)
    {
        var size = ChatRules.PageSize(top);
        return size.IsSuccess
            ? messages.DeltaAsync(teamId, channelId, token, size.Value, cancellationToken)
            : Task.FromResult<Result<DeltaPage<ChatMessage>>>(size.Error);
    }
}

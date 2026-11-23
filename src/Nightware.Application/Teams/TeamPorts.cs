using Nightware.Application.Chats;
using Nightware.Domain.Chats;
using Nightware.Domain.Kernel;
using Nightware.Domain.Teams;

namespace Nightware.Application.Teams;

/// <summary>
/// The caller's teams over the team backend (ADR 0009 D5), as the caller. A team id the backend doesn't know, or a team
/// the caller isn't in, is <see cref="ErrorKind.NotFound"/>; changing a team the caller doesn't own is
/// <see cref="ErrorKind.Forbidden"/>.
/// </summary>
public interface ITeamStore
{
    Task<Result<IReadOnlyList<Team>>> ListJoinedAsync(CancellationToken cancellationToken);

    Task<Result<Team>> GetAsync(string teamId, CancellationToken cancellationToken);

    /// <summary>Creates the team with the caller as its owner, adds <see cref="TeamCreation.Members"/>, and creates its General channel.</summary>
    Task<Result<Team>> CreateAsync(TeamCreation creation, CancellationToken cancellationToken);

    Task<Result> UpdateAsync(string teamId, TeamUpdate update, CancellationToken cancellationToken);

    /// <summary>Deletes the team and its channels.</summary>
    Task<Result> DeleteAsync(string teamId, CancellationToken cancellationToken);
}

/// <summary>
/// A team's members (Graph <c>aadUserConversationMember</c>s). Membership ids are the store's own; an unknown one is
/// <see cref="ErrorKind.NotFound"/>. The team's creator can be neither demoted nor removed (Graph's "last owner" rule,
/// <see cref="ErrorKind.Validation"/>).
/// </summary>
public interface ITeamMembers
{
    Task<Result<IReadOnlyList<ChatMember>>> ListAsync(string teamId, CancellationToken cancellationToken);

    Task<Result<ChatMember>> GetAsync(string teamId, string membershipId, CancellationToken cancellationToken);

    /// <summary>Adds a directory user; a user the directory doesn't know is <see cref="ErrorKind.NotFound"/>.</summary>
    Task<Result<ChatMember>> AddAsync(string teamId, ChatMemberDraft member, CancellationToken cancellationToken);

    /// <summary>Makes a member an owner or an ordinary member.</summary>
    Task<Result<ChatMember>> SetOwnerAsync(string teamId, string membershipId, bool isOwner, CancellationToken cancellationToken);

    Task<Result> RemoveAsync(string teamId, string membershipId, CancellationToken cancellationToken);
}

/// <summary>
/// A team's standard channels (ADR 0009 D5). A channel id that isn't one of the team's is <see cref="ErrorKind.NotFound"/>.
/// The primary channel can't be renamed or deleted (<see cref="ErrorKind.Validation"/>).
/// </summary>
public interface IChannelStore
{
    /// <summary>The team's channels, the primary one first.</summary>
    Task<Result<IReadOnlyList<Channel>>> ListAsync(string teamId, CancellationToken cancellationToken);

    Task<Result<Channel>> GetAsync(string teamId, string channelId, CancellationToken cancellationToken);

    /// <summary>The team's General channel; NotFound when the team has no channel at all.</summary>
    Task<Result<Channel>> GetPrimaryAsync(string teamId, CancellationToken cancellationToken);

    Task<Result<Channel>> CreateAsync(string teamId, ChannelCreation creation, CancellationToken cancellationToken);

    Task<Result> UpdateAsync(string teamId, string channelId, ChannelUpdate update, CancellationToken cancellationToken);

    Task<Result> DeleteAsync(string teamId, string channelId, CancellationToken cancellationToken);

    /// <summary>
    /// Where the channel's files folder is in the caller's drive: a path from the drive's root (the team's folder, then
    /// the channel's name). The folder itself may not exist yet.
    /// </summary>
    Task<Result<IReadOnlyList<string>>> FilesFolderPathAsync(string teamId, string channelId, CancellationToken cancellationToken);
}

/// <summary>
/// A channel message: a post (<see cref="ReplyId"/> null) or one of its replies.
/// </summary>
public sealed record ChannelMessageRef(string TeamId, string ChannelId, string MessageId, string? ReplyId = null)
{
    public bool IsReply => ReplyId is not null;
}

/// <summary>
/// A channel's posts and replies (ADR 0009 D4–D5). Reading never moves the caller's read marker. A post id that isn't a
/// post of the channel, or a reply id that isn't a reply to that post, is <see cref="ErrorKind.NotFound"/>.
/// </summary>
public interface IChannelMessages
{
    /// <summary>The channel's posts, newest first.</summary>
    Task<Result<Page<ChatMessage>>> ListPostsAsync(string teamId, string channelId, ChatPageQuery query, CancellationToken cancellationToken);

    /// <summary>A post's replies, newest first.</summary>
    Task<Result<Page<ChatMessage>>> ListRepliesAsync(ChannelMessageRef post, ChatPageQuery query, CancellationToken cancellationToken);

    Task<Result<ChatMessage>> GetAsync(ChannelMessageRef message, CancellationToken cancellationToken);

    /// <summary>Starts a post; its <see cref="ChatMessageDraft.Subject"/> is the post's subject.</summary>
    Task<Result<ChatMessage>> PostAsync(string teamId, string channelId, ChatMessageDraft draft, CancellationToken cancellationToken);

    /// <summary>Replies to a post. A post that can't take replies (one sent outside a thread) is <see cref="ErrorKind.Validation"/>.</summary>
    Task<Result<ChatMessage>> ReplyAsync(ChannelMessageRef post, ChatMessageDraft draft, CancellationToken cancellationToken);

    /// <summary>Replaces the body of one of the caller's messages; a post's subject too, when the draft has one.</summary>
    Task<Result> UpdateAsync(ChannelMessageRef message, ChatMessageDraft draft, CancellationToken cancellationToken);

    Task<Result> SoftDeleteAsync(ChannelMessageRef message, CancellationToken cancellationToken);

    /// <param name="reaction">An emoji.</param>
    Task<Result> SetReactionAsync(ChannelMessageRef message, string reaction, CancellationToken cancellationToken);

    Task<Result> UnsetReactionAsync(ChannelMessageRef message, string reaction, CancellationToken cancellationToken);

    /// <summary>
    /// The channel's posts that are new or changed since <paramref name="token"/> (null: every post, newest first).
    /// A token this store didn't issue for this channel is <see cref="ErrorKind.SyncStateGone"/>.
    /// </summary>
    Task<Result<DeltaPage<ChatMessage>>> DeltaAsync(string teamId, string channelId, DeltaToken? token, int pageSize, CancellationToken cancellationToken);
}

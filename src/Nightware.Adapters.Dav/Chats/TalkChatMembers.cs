using Nightware.Adapters.Dav.Chats.Talk;
using Nightware.Application.Chats;
using Nightware.Domain.Chats;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Chats;

/// <summary>
/// <see cref="IChatMembers"/> on Talk participants (ADR 0009 D3): the room's participants of type <c>users</c>; an
/// owner or moderator is an <c>owner</c>. A membership id is <c>m.</c> plus the attendee id, which Talk gives anew
/// each time someone is added.
/// </summary>
internal sealed class TalkChatMembers(TalkChatClient talk, TalkIdentities identities) : IChatMembers
{
    public async Task<Result<IReadOnlyList<ChatMember>>> ListAsync(string chatId, CancellationToken cancellationToken)
    {
        var participants = await UsersAsync(chatId, cancellationToken).ConfigureAwait(false);
        return participants.IsSuccess ? participants.Value.Select(ToMember).ToList() : participants.Error;
    }

    public async Task<Result<ChatMember>> GetAsync(string chatId, string membershipId, CancellationToken cancellationToken)
    {
        if (!TalkIds.TryAttendeeId(membershipId, out var attendeeId))
        {
            return NoMember();
        }

        var participants = await UsersAsync(chatId, cancellationToken).ConfigureAwait(false);
        if (!participants.IsSuccess)
        {
            return participants.Error;
        }

        return participants.Value.FirstOrDefault(p => p.AttendeeId == attendeeId) is { } participant ? ToMember(participant) : NoMember();
    }

    public async Task<Result<ChatMember>> AddAsync(string chatId, ChatMemberDraft member, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(member);
        var accountName = await identities.AccountNameOfAsync(member.User, cancellationToken).ConfigureAwait(false);
        if (!accountName.IsSuccess)
        {
            return accountName.Error;
        }

        var added = await talk.AddUserAsync(chatId, accountName.Value, cancellationToken).ConfigureAwait(false);
        if (!added.IsSuccess)
        {
            return added.Error!;
        }

        // Talk answers an addition with no body, so the new attendee is read back.
        var participants = await UsersAsync(chatId, cancellationToken).ConfigureAwait(false);
        if (!participants.IsSuccess)
        {
            return participants.Error;
        }

        if (participants.Value.FirstOrDefault(p => p.Actor.Id == accountName.Value) is not { } participant)
        {
            return Error.BackendUnavailable("Nextcloud Talk did not list the member it added.");
        }

        if (member.IsOwner && !TalkParticipantType.Moderates(participant.ParticipantType))
        {
            var promoted = await talk.PromoteAsync(chatId, participant.AttendeeId, cancellationToken).ConfigureAwait(false);
            if (!promoted.IsSuccess)
            {
                return promoted.Error!;
            }

            participant = participant with { ParticipantType = TalkParticipantType.Moderator };
        }

        return ToMember(participant);
    }

    public async Task<Result> RemoveAsync(string chatId, string membershipId, CancellationToken cancellationToken)
    {
        if (!TalkIds.TryAttendeeId(membershipId, out var attendeeId))
        {
            return NoMember();
        }

        // Talk lets nobody remove themselves as an attendee: the caller's own membership is left instead.
        var room = await talk.GetRoomAsync(chatId, cancellationToken).ConfigureAwait(false);
        if (!room.IsSuccess)
        {
            return room.Error!;
        }

        var removed = room.Value.AttendeeId == attendeeId
            ? await talk.LeaveAsync(chatId, cancellationToken).ConfigureAwait(false)
            : await talk.RemoveAttendeeAsync(chatId, attendeeId, cancellationToken).ConfigureAwait(false);
        return removed.IsSuccess || removed.Error!.Kind != ErrorKind.NotFound ? removed : NoMember();
    }

    private async Task<Result<IReadOnlyList<TalkParticipant>>> UsersAsync(string chatId, CancellationToken cancellationToken)
    {
        var participants = await talk.ParticipantsAsync(chatId, cancellationToken).ConfigureAwait(false);
        if (!participants.IsSuccess)
        {
            return participants.Error;
        }

        var users = participants.Value.Where(p => p.Actor.IsUser).ToList();
        var loaded = await identities.LoadAsync(users.Select(p => p.Actor.Id), cancellationToken).ConfigureAwait(false);
        return loaded.IsSuccess ? users : loaded.Error!;
    }

    private ChatMember ToMember(TalkParticipant participant)
    {
        var user = identities.UserOf(participant.Actor.Id);
        return new ChatMember(TalkIds.MembershipId(participant.AttendeeId), user?.DisplayName ?? participant.Actor.DisplayName)
        {
            UserId = user?.Id,
            Email = user?.Mail,
            Roles = TalkParticipantType.Moderates(participant.ParticipantType) ? [ChatMember.OwnerRole] : [],
        };
    }

    private static Error NoMember() => Error.NotFound("The member was not found in this chat.");
}

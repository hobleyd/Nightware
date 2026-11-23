using Nightware.Adapters.Dav.Chats.Talk;
using Nightware.Application.Teams;
using Nightware.Domain.Chats;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Teams;

/// <summary>
/// <see cref="ITeamMembers"/> on a circle's members (ADR 0009 D5): its users (groups and other kinds of member aren't
/// Graph members); level 8 or 9 is an <c>owner</c>. A membership id is <c>m.</c> plus the circle member id. The circle's
/// owner (level 9) can be neither demoted nor removed.
/// </summary>
internal sealed class NextcloudTeamMembers(NextcloudCircles circles, TalkIdentities identities) : ITeamMembers
{
    public const string MembershipPrefix = "m.";

    public async Task<Result<IReadOnlyList<ChatMember>>> ListAsync(string teamId, CancellationToken cancellationToken)
    {
        var members = await UsersAsync(teamId, cancellationToken).ConfigureAwait(false);
        return members.IsSuccess ? members.Value.Users.Select(ToMember).ToList() : members.Error;
    }

    public async Task<Result<ChatMember>> GetAsync(string teamId, string membershipId, CancellationToken cancellationToken)
    {
        var member = await FindAsync(teamId, membershipId, cancellationToken).ConfigureAwait(false);
        return member.IsSuccess ? ToMember(member.Value.Member) : member.Error;
    }

    public async Task<Result<ChatMember>> AddAsync(string teamId, ChatMemberDraft member, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(member);
        var circle = await circles.GetAsync(teamId, cancellationToken).ConfigureAwait(false);
        if (!circle.IsSuccess)
        {
            return circle.Error;
        }

        var accountName = await identities.AccountNameOfAsync(member.User, cancellationToken).ConfigureAwait(false);
        if (!accountName.IsSuccess)
        {
            return accountName.Error;
        }

        var added = await circles.AddUserAsync(teamId, accountName.Value, cancellationToken).ConfigureAwait(false);
        if (!added.IsSuccess)
        {
            return added.Error;
        }

        var circleMember = added.Value;
        if (member.IsOwner && !CircleLevel.IsOwnerRole(circleMember.Level))
        {
            var promoted = await circles.SetLevelAsync(teamId, circleMember.Id, CircleLevel.Admin, cancellationToken).ConfigureAwait(false);
            if (!promoted.IsSuccess)
            {
                return promoted.Error!;
            }

            circleMember = circleMember with { Level = CircleLevel.Admin };
        }

        var loaded = await identities.LoadAsync([circleMember.UserId], cancellationToken).ConfigureAwait(false);
        return loaded.IsSuccess ? ToMember(circleMember) : loaded.Error!;
    }

    public async Task<Result<ChatMember>> SetOwnerAsync(string teamId, string membershipId, bool isOwner, CancellationToken cancellationToken)
    {
        var member = await FindAsync(teamId, membershipId, cancellationToken).ConfigureAwait(false);
        if (!member.IsSuccess)
        {
            return member.Error;
        }

        var current = member.Value.Member;
        if (CircleLevel.IsOwnerRole(current.Level) == isOwner)
        {
            return ToMember(current);
        }

        if (current.Level == CircleLevel.Owner)
        {
            return LastOwner("demoted");
        }

        var level = isOwner ? CircleLevel.Admin : CircleLevel.Member;
        var set = await circles.SetLevelAsync(teamId, current.Id, level, cancellationToken).ConfigureAwait(false);
        return set.IsSuccess ? ToMember(current with { Level = level }) : set.Error!;
    }

    public async Task<Result> RemoveAsync(string teamId, string membershipId, CancellationToken cancellationToken)
    {
        var member = await FindAsync(teamId, membershipId, cancellationToken).ConfigureAwait(false);
        if (!member.IsSuccess)
        {
            return member.Error;
        }

        var (circle, target) = member.Value;
        if (target.Level == CircleLevel.Owner)
        {
            return LastOwner("removed");
        }

        // The circles app lets a member leave but not remove themselves.
        return target.Id == circle.Caller?.Id
            ? await circles.LeaveAsync(teamId, cancellationToken).ConfigureAwait(false)
            : await circles.RemoveMemberAsync(teamId, target.Id, cancellationToken).ConfigureAwait(false);
    }

    public static bool TryMemberId(string? membershipId, out string memberId)
    {
        memberId = string.Empty;
        if (membershipId is null || !membershipId.StartsWith(MembershipPrefix, StringComparison.Ordinal)
            || !NextcloudCircles.IsMemberId(membershipId[MembershipPrefix.Length..]))
        {
            return false;
        }

        memberId = membershipId[MembershipPrefix.Length..];
        return true;
    }

    private async Task<Result<(Circle Circle, CircleMember Member)>> FindAsync(string teamId, string membershipId, CancellationToken cancellationToken)
    {
        if (!TryMemberId(membershipId, out var memberId))
        {
            return NextcloudCircles.NoMember();
        }

        var members = await UsersAsync(teamId, cancellationToken).ConfigureAwait(false);
        if (!members.IsSuccess)
        {
            return members.Error;
        }

        return members.Value.Users.FirstOrDefault(m => m.Id == memberId) is { } member ? (members.Value.Circle, member) : NextcloudCircles.NoMember();
    }

    /// <summary>The circle's user members, with their directory identities loaded. The circle is checked to be the caller's first.</summary>
    private async Task<Result<(Circle Circle, IReadOnlyList<CircleMember> Users)>> UsersAsync(string teamId, CancellationToken cancellationToken)
    {
        var circle = await circles.GetAsync(teamId, cancellationToken).ConfigureAwait(false);
        if (!circle.IsSuccess)
        {
            return circle.Error;
        }

        var members = await circles.MembersAsync(teamId, cancellationToken).ConfigureAwait(false);
        if (!members.IsSuccess)
        {
            return members.Error;
        }

        var users = members.Value.Where(m => m.IsUser && m.Level > 0).ToList();
        var loaded = await identities.LoadAsync(users.Select(m => m.UserId), cancellationToken).ConfigureAwait(false);
        return loaded.IsSuccess ? (circle.Value, users) : loaded.Error!;
    }

    private ChatMember ToMember(CircleMember member)
    {
        var user = identities.UserOf(member.UserId);
        return new ChatMember(MembershipPrefix + member.Id, user?.DisplayName ?? member.DisplayName)
        {
            UserId = user?.Id,
            Email = user?.Mail,
            Roles = CircleLevel.IsOwnerRole(member.Level) ? [ChatMember.OwnerRole] : [],
        };
    }

    private static Error LastOwner(string what) =>
        Error.Validation($"The team's creator is its last owner and can't be {what}.", "roles");
}

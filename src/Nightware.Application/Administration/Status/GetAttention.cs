using Nightware.Application.Administration.Entities;
using Nightware.Application.Administration.Users;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Status;

/// <summary>A user whose status needs an admin, in the entity they were found in.</summary>
public sealed record UserNeedingAttention(UserDetail User, UserStatus Status, Entity Entity);

/// <summary>A domain the mail server does not serve, or serves without signing keys.</summary>
public sealed record DomainNeedingAttention(DomainStatus Domain, Entity Entity);

/// <summary>
/// What needs an admin across their entities. <see cref="NotChecked"/> names the entities that were too large
/// to read in full or could not be read, so an empty list is never mistaken for "all clear".
/// </summary>
public sealed record Attention(IReadOnlyList<UserNeedingAttention> Users, IReadOnlyList<DomainNeedingAttention> Domains, IReadOnlyList<Entity> NotChecked)
{
    public bool IsClear => Users.Count == 0 && Domains.Count == 0 && NotChecked.Count == 0;
}

/// <summary>
/// The dashboard's worklist: every user of the actor's entities whose offboarding is incomplete, who conflicts
/// or who needs repair, and every domain not served properly. Read on each visit, stored nowhere (ADR 0002 D1);
/// an entity above <see cref="ListUsers"/>' link limit is reported as not checked rather than half-read.
/// </summary>
public sealed class GetAttention(ListEntities entities, GetEntity getEntity, ListUsers listUsers)
{
    public async Task<Result<Attention>> ExecuteAsync(CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<Entity>> manageable = await entities.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        if (!manageable.IsSuccess)
        {
            return manageable.Error;
        }

        var users = new List<UserNeedingAttention>();
        var domains = new List<DomainNeedingAttention>();
        var notChecked = new List<Entity>();
        foreach (Entity entity in manageable.Value)
        {
            Result<EntityDetail> detail = await getEntity.ExecuteAsync(entity.Id, cancellationToken).ConfigureAwait(false);
            Result<IReadOnlyList<UserDetail>> members = await listUsers.ExecuteAsync(entity.Id, cancellationToken).ConfigureAwait(false);
            if (!detail.IsSuccess || !members.IsSuccess || members.Value.Count > ListUsers.LinkedListLimit)
            {
                notChecked.Add(entity);
            }

            if (detail.IsSuccess)
            {
                domains.AddRange(detail.Value.Domains
                    .Where(static d => d.Mail.State != LinkState.Linked || !d.DkimPresent)
                    .Select(d => new DomainNeedingAttention(d, entity)));
            }

            if (members.IsSuccess && members.Value.Count <= ListUsers.LinkedListLimit)
            {
                users.AddRange(members.Value
                    .Select(u => new UserNeedingAttention(u, UserStatuses.Of(u), entity))
                    .Where(static u => u.Status is UserStatus.OffboardingIncomplete or UserStatus.Conflict or UserStatus.NeedsRepair));
            }
        }

        // The most urgent first: an open way in, then accounts that disagree, then repairs.
        users.Sort(static (a, b) => Rank(a.Status).CompareTo(Rank(b.Status)) is var byRank and not 0
            ? byRank
            : string.Compare(a.User.User.Identity.DisplayName, b.User.User.Identity.DisplayName, StringComparison.OrdinalIgnoreCase));
        return new Attention(users, domains, notChecked);
    }

    private static int Rank(UserStatus status) => status switch
    {
        UserStatus.OffboardingIncomplete => 0,
        UserStatus.Conflict => 1,
        _ => 2,
    };
}

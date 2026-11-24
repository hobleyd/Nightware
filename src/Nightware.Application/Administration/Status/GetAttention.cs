using Nightware.Application.Administration.Entities;
using Nightware.Application.Administration.Groups;
using Nightware.Application.Administration.SharedMailboxes;
using Nightware.Application.Administration.Users;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Status;

/// <summary>A user whose status needs an admin, in the entity they were found in.</summary>
public sealed record UserNeedingAttention(UserDetail User, UserStatus Status, Entity Entity);

/// <summary>A domain the mail server does not serve, or serves without signing keys, or whose records are not yet in its zone at the entity's DNS host.</summary>
/// <param name="Unsafe">What in the domain's own SPF or DMARC lets anyone send as it; empty when nothing does.</param>
/// <param name="DnsChanges">The changes publishing to the entity's DNS host would write; 0 when none, or when not compared.</param>
public sealed record DomainNeedingAttention(DomainStatus Domain, Entity Entity, IReadOnlyList<MailAuthenticationWeakness> Unsafe, int DnsChanges = 0)
{
    /// <summary>The mail server's side is what needs fixing (Repair), not the domain's own records.</summary>
    public bool NeedsRepair => Domain.Mail.State != LinkState.Linked || !Domain.DkimPresent;
}

/// <summary>A shared mailbox a system is missing, out of step with its members or could not be checked for; Repair fixes it.</summary>
public sealed record MailboxNeedingAttention(SharedMailboxDetail Mailbox)
{
    public Entity Entity => Mailbox.Entity;
}

/// <summary>A group whose list or Nextcloud group a system is missing, is out of step with its members or could not be checked for; Repair fixes it.</summary>
public sealed record GroupNeedingAttention(GroupDetail Group)
{
    public Entity Entity => Group.Entity;
}

/// <summary>
/// What needs an admin across their entities. <see cref="NotChecked"/> names the entities that were too large
/// to read in full or could not be read, so an empty list is never mistaken for "all clear".
/// </summary>
/// <param name="UserCounts">Each entity's number of users, by entity id; absent for an entity whose users could not be read.</param>
public sealed record Attention(IReadOnlyList<UserNeedingAttention> Users, IReadOnlyList<DomainNeedingAttention> Domains, IReadOnlyList<MailboxNeedingAttention> Mailboxes, IReadOnlyList<GroupNeedingAttention> Groups, IReadOnlyList<Entity> NotChecked, IReadOnlyDictionary<string, int> UserCounts)
{
    public bool IsClear => Users.Count == 0 && Domains.Count == 0 && Mailboxes.Count == 0 && Groups.Count == 0 && NotChecked.Count == 0;
}

/// <summary>
/// The dashboard's worklist: every user of the actor's entities whose offboarding is incomplete, who conflicts
/// or who needs repair, every domain not served properly, and every shared mailbox and group that needs Repair. Read on each visit, stored nowhere (ADR 0002 D1);
/// an entity above <see cref="ListUsers"/>' link limit is reported as not checked rather than half-read. For a
/// global administrator, a served domain of an entity connected to a DNS host is also compared with its zone, and
/// needs attention while publishing would change it (publishing is theirs alone); a zone that cannot be read leaves
/// its entity not checked, as does a shared mailbox or group that cannot be read.
/// </summary>
public sealed class GetAttention(
    IAdminActor actor,
    ListEntities entities,
    GetEntity getEntity,
    ListUsers listUsers,
    PlanDnsPublication planPublication,
    ListSharedMailboxes listMailboxes,
    GetSharedMailbox getMailbox,
    ListGroups listGroups,
    GetGroup getGroup)
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
        var mailboxes = new List<MailboxNeedingAttention>();
        var groups = new List<GroupNeedingAttention>();
        var notChecked = new List<Entity>();
        var userCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Entity entity in manageable.Value)
        {
            Result<EntityDetail> detail = await getEntity.ExecuteAsync(entity.Id, cancellationToken).ConfigureAwait(false);
            Result<IReadOnlyList<UserDetail>> members = await listUsers.ExecuteAsync(entity.Id, cancellationToken).ConfigureAwait(false);
            if (!detail.IsSuccess || !members.IsSuccess || members.Value.Count > ListUsers.LinkedListLimit)
            {
                notChecked.Add(entity);
            }

            if (members.IsSuccess)
            {
                userCounts[entity.Id] = members.Value.Count;
            }

            if (detail.IsSuccess)
            {
                // Weak records are shown where they are read; only one that lets anyone send as the domain needs you now.
                foreach (DomainStatus d in detail.Value.Domains)
                {
                    int? dnsChanges = await DnsChangesAsync(detail.Value.Entity, d, cancellationToken).ConfigureAwait(false);
                    if (dnsChanges is null && !notChecked.Contains(entity))
                    {
                        notChecked.Add(entity);
                    }

                    var domain = new DomainNeedingAttention(d, entity, UnsafeIn(detail.Value.Entity.MailPolicyFor(d.Domain)), dnsChanges ?? 0);
                    if (domain.NeedsRepair || domain.Unsafe.Count > 0 || domain.DnsChanges > 0)
                    {
                        domains.Add(domain);
                    }
                }
            }

            if (members.IsSuccess && members.Value.Count <= ListUsers.LinkedListLimit)
            {
                users.AddRange(members.Value
                    .Select(u => new UserNeedingAttention(u, UserStatuses.Of(u), entity))
                    .Where(static u => u.Status is UserStatus.OffboardingIncomplete or UserStatus.Conflict or UserStatus.EmailNotVerified or UserStatus.NeedsRepair));
            }

            if ((!await AddMailboxesAsync(entity, mailboxes, cancellationToken).ConfigureAwait(false)
                    | !await AddGroupsAsync(entity, groups, cancellationToken).ConfigureAwait(false))
                && !notChecked.Contains(entity))
            {
                notChecked.Add(entity);
            }
        }

        // The most urgent first: an open way in, then accounts that disagree, then repairs.
        users.Sort(static (a, b) => Rank(a.Status).CompareTo(Rank(b.Status)) is var byRank and not 0
            ? byRank
            : string.Compare(a.User.User.Identity.DisplayName, b.User.User.Identity.DisplayName, StringComparison.OrdinalIgnoreCase));
        return new Attention(users, domains, mailboxes, groups, notChecked, userCounts);
    }

    /// <summary>Adds the entity's shared mailboxes that need Repair, by name; false when one (or the list) could not be read.</summary>
    private async Task<bool> AddMailboxesAsync(Entity entity, List<MailboxNeedingAttention> found, CancellationToken cancellationToken)
    {
        Result<(Entity Entity, IReadOnlyList<SharedMailbox> Mailboxes)> listed = await listMailboxes.ExecuteAsync(entity.Id, cancellationToken).ConfigureAwait(false);
        if (!listed.IsSuccess)
        {
            return false;
        }

        bool complete = true;
        foreach (SharedMailbox mailbox in listed.Value.Mailboxes)
        {
            Result<SharedMailboxDetail> detail = await getMailbox.ExecuteAsync(entity.Id, mailbox.Id, cancellationToken).ConfigureAwait(false);
            if (!detail.IsSuccess)
            {
                complete = false;
            }
            else if (detail.Value.NeedsRepair)
            {
                found.Add(new MailboxNeedingAttention(detail.Value));
            }
        }

        return complete;
    }

    /// <summary>Adds the entity's groups that need Repair, by name; false when one (or the list) could not be read.</summary>
    private async Task<bool> AddGroupsAsync(Entity entity, List<GroupNeedingAttention> found, CancellationToken cancellationToken)
    {
        Result<(Entity Entity, IReadOnlyList<Group> Groups)> listed = await listGroups.ExecuteAsync(entity.Id, cancellationToken).ConfigureAwait(false);
        if (!listed.IsSuccess)
        {
            return false;
        }

        bool complete = true;
        foreach (Group group in listed.Value.Groups)
        {
            Result<GroupDetail> detail = await getGroup.ExecuteAsync(entity.Id, group.Id, cancellationToken).ConfigureAwait(false);
            if (!detail.IsSuccess)
            {
                complete = false;
            }
            else if (detail.Value.NeedsRepair)
            {
                found.Add(new GroupNeedingAttention(detail.Value));
            }
        }

        return complete;
    }

    /// <summary>
    /// How many changes publishing the domain's records to the entity's DNS host would write; 0 when it is
    /// not compared, null when the zone could not be read (the entity is then not checked; its DNS page says why).
    /// </summary>
    private async Task<int?> DnsChangesAsync(Entity entity, DomainStatus domain, CancellationToken cancellationToken)
    {
        if (!actor.Current.IsGlobal || !entity.HasDnsToken || domain.Mail.State != LinkState.Linked)
        {
            return 0;
        }

        Result<DnsPublicationPreview> preview = await planPublication.ExecuteAsync(entity.Id, domain.Domain.Name, cancellationToken).ConfigureAwait(false);
        return preview.IsSuccess ? preview.Value.Plan.Changes.Count(static c => c.IsWrite) : null;
    }

    private static int Rank(UserStatus status) => status switch
    {
        UserStatus.OffboardingIncomplete => 0,
        UserStatus.Conflict => 1,
        UserStatus.EmailNotVerified => 2,
        _ => 3,
    };

    private static MailAuthenticationWeakness[] UnsafeIn(DomainMailPolicy policy) =>
        [
            .. (policy.Spf is { } spf ? MailAuthenticationWeaknesses.Spf(spf.Value) : [])
                .Concat(policy.Dmarc is { } dmarc ? MailAuthenticationWeaknesses.Dmarc(dmarc.Value) : [])
                .Where(static w => w.Severity == WeaknessSeverity.Unsafe),
        ];
}

using Nightware.Application.Administration.Entities;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Users;

/// <summary>An entity's members with their link state in every backend.</summary>
public sealed class ListUsers(IAdminActor actor, IEntityRegistry entities, UserAccess users)
{
    /// <summary>Above this many members the list shows Keycloak only; a user's page always shows every backend.</summary>
    internal const int LinkedListLimit = 200;

    private const int Parallelism = 8;

    public async Task<Result<IReadOnlyList<UserDetail>>> ExecuteAsync(string entityId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        Result allowed = AdminAuthorization.RequireEntity(actor.Current, entityId);
        if (!allowed.IsSuccess)
        {
            return allowed.Error;
        }

        Result<IReadOnlyList<IdentityAccount>> members = await entities.ListMembersAsync(entityId, cancellationToken).ConfigureAwait(false);
        if (!members.IsSuccess)
        {
            return members.Error;
        }

        IdentityAccount[] ordered = [.. members.Value.OrderBy(static m => m.Email, StringComparer.OrdinalIgnoreCase)];
        var details = new UserDetail[ordered.Length];
        if (ordered.Length > LinkedListLimit)
        {
            for (int i = 0; i < ordered.Length; i++)
            {
                details[i] = new UserDetail(new ManagedUser(ordered[i], entityId, false, false), [LinkStateEvaluator.Identity(ordered[i])]);
            }

            return details;
        }

        await Parallel.ForAsync(0, ordered.Length, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken }, async (i, ct) =>
            details[i] = await users.WithLinksAsync(new ManagedUser(ordered[i], entityId, false, false), ct).ConfigureAwait(false)).ConfigureAwait(false);
        return details;
    }
}

/// <summary>
/// One user with their entity, admin roles and link state in every backend. A global admin also sees an account
/// the console never changes, marked <see cref="UserDetail.OffLimits"/>; every action on it is still refused.
/// </summary>
public sealed class GetUser(UserAccess users)
{
    public Task<Result<UserDetail>> ExecuteAsync(string userId, CancellationToken cancellationToken) =>
        users.ViewWithLinksAsync(userId, cancellationToken);
}

/// <summary>A user found by <see cref="FindUsers"/>, with the entity they were found in and their state everywhere.</summary>
public sealed record FoundUser(UserDetail User, Entity Entity);

/// <summary>What <see cref="FindUsers"/> found: at most <see cref="FindUsers.Limit"/> users, and whether there were more.</summary>
public sealed record FoundUsers(IReadOnlyList<FoundUser> Users, bool Truncated);

/// <summary>
/// The users, across every entity the actor manages, whose name, username or address (aliases included)
/// contains the text: the console's "find a user", so an admin need not remember which entity someone is in.
/// Matches against Keycloak, then reads the first <see cref="Limit"/> matches' state in every backend, so the
/// result can say truthfully whether each one is offboarded.
/// </summary>
public sealed class FindUsers(ListEntities entities, IEntityRegistry registry, UserAccess users)
{
    /// <summary>The most matches whose backend state is read; a broader search is asked to narrow.</summary>
    public const int Limit = 25;

    public async Task<Result<FoundUsers>> ExecuteAsync(string? text, CancellationToken cancellationToken)
    {
        Result<IReadOnlyList<Entity>> manageable = await entities.ExecuteAsync(cancellationToken).ConfigureAwait(false);
        if (!manageable.IsSuccess)
        {
            return manageable.Error;
        }

        string q = text?.Trim() ?? string.Empty;
        if (q.Length == 0)
        {
            return new FoundUsers([], false);
        }

        var found = new Dictionary<string, (IdentityAccount Identity, Entity Entity)>(StringComparer.Ordinal);
        foreach (Entity entity in manageable.Value)
        {
            Result<IReadOnlyList<IdentityAccount>> members = await registry.ListMembersAsync(entity.Id, cancellationToken).ConfigureAwait(false);
            if (!members.IsSuccess)
            {
                return members.Error;
            }

            foreach (IdentityAccount member in members.Value.Where(m => Matches(m, q)))
            {
                found.TryAdd(member.Id, (member, entity));
            }
        }

        (IdentityAccount Identity, Entity Entity)[] first = [.. found.Values.OrderBy(static f => f.Identity.DisplayName, StringComparer.OrdinalIgnoreCase).Take(Limit)];
        var result = new FoundUser[first.Length];
        await Parallel.ForAsync(0, first.Length, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken }, async (i, ct) =>
            result[i] = new FoundUser(await users.WithLinksAsync(new ManagedUser(first[i].Identity, first[i].Entity.Id, false, false), ct).ConfigureAwait(false), first[i].Entity)).ConfigureAwait(false);
        return new FoundUsers(result, found.Count > Limit);
    }

    private static bool Matches(IdentityAccount user, string q) =>
        user.Email.Contains(q, StringComparison.OrdinalIgnoreCase)
        || user.Username.Contains(q, StringComparison.OrdinalIgnoreCase)
        || user.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
        || user.Aliases.Any(a => a.Contains(q, StringComparison.OrdinalIgnoreCase));
}

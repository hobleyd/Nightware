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

/// <summary>One user with their entity, admin roles and link state in every backend.</summary>
public sealed class GetUser(UserAccess users)
{
    public Task<Result<UserDetail>> ExecuteAsync(string userId, CancellationToken cancellationToken) =>
        users.LoadWithLinksAsync(userId, cancellationToken);
}

using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Entities;

/// <summary>The entities the actor may manage: all for a global admin, their own for an entity admin.</summary>
public sealed class ListEntities(IAdminActor actor, IEntityRegistry entities)
{
    public async Task<Result<IReadOnlyList<Entity>>> ExecuteAsync(CancellationToken cancellationToken)
    {
        Result admin = AdminAuthorization.RequireAdmin(actor.Current);
        if (!admin.IsSuccess)
        {
            return admin.Error;
        }

        Result<IReadOnlyList<Entity>> all = await entities.ListAsync(cancellationToken).ConfigureAwait(false);
        return all.Map(list => (IReadOnlyList<Entity>)[.. list.Where(e => actor.Current.MayManage(e.Id)).OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)]);
    }
}

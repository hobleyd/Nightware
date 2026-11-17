using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Entities;

/// <summary>One entity with its domains' mail-server status.</summary>
public sealed class GetEntity(IAdminActor actor, IEntityRegistry entities, DomainProvisioning domains)
{
    public async Task<Result<EntityDetail>> ExecuteAsync(string entityId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        Result allowed = AdminAuthorization.RequireEntity(actor.Current, entityId);
        if (!allowed.IsSuccess)
        {
            return allowed.Error;
        }

        Result<Entity> entity = await entities.GetAsync(entityId, cancellationToken).ConfigureAwait(false);
        return entity.IsSuccess ? await domains.DetailAsync(entity.Value, cancellationToken).ConfigureAwait(false) : entity.Error;
    }
}

using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Users;

/// <summary>The wizard's second step: what applying the request would create, link or stop on.</summary>
public sealed class PlanUserProvisioning(ProvisioningPlanner planner)
{
    public async Task<Result<ProvisioningPlan>> ExecuteAsync(NewUserRequest request, CancellationToken cancellationToken) =>
        (await planner.PlanAsync(request, cancellationToken).ConfigureAwait(false)).Map(static p => p.Plan);
}

using Nightware.Domain.Kernel;

namespace Nightware.Application.Administration.Status;

/// <summary>Whether the console can reach and authenticate to each backend, for the dashboard.</summary>
public sealed class GetSystemStatus(IAdminActor actor, IEnumerable<IBackendStatus> backends)
{
    public async Task<Result<IReadOnlyList<BackendHealth>>> ExecuteAsync(CancellationToken cancellationToken)
    {
        Result allowed = AdminAuthorization.RequireAdmin(actor.Current);
        if (!allowed.IsSuccess)
        {
            return allowed.Error;
        }

        BackendHealth[] health = await Task.WhenAll(backends.Select(b => b.CheckAsync(cancellationToken))).ConfigureAwait(false);
        return health.OrderBy(static h => h.Backend).ToArray();
    }
}

using Nightware.Adapters.Jmap.Core.Requests;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Administration;

/// <summary>Whether the console can reach Stalwart and its management principal is accepted: one cheap <c>x:Account/query</c>.</summary>
internal sealed class StalwartBackendStatus(IStalwartManagementClient client) : IBackendStatus
{
    public async Task<BackendHealth> CheckAsync(CancellationToken cancellationToken)
    {
        var request = new JmapRequestBuilder();
        var query = request.Add(StalwartMethods.AccountQuery, new StalwartQueryRequest<AccountFilter> { Limit = 1 });
        Result<JmapResponse> response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        Result checkedQuery = response.Bind(r => r.Get(query)).ToResult();
        return checkedQuery.IsSuccess
            ? new BackendHealth(BackendKind.Mail, Healthy: true)
            : new BackendHealth(BackendKind.Mail, Healthy: false, checkedQuery.Error.Message);
    }
}
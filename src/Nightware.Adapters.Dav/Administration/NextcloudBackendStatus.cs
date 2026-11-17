using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Logging;

using Nightware.Application.Administration;
using Nightware.Domain.Administration;

namespace Nightware.Adapters.Dav.Administration;

/// <summary>
/// Whether the console can reach Nextcloud and sign in with its own account: <c>GET /ocs/v1.php/cloud/user</c>
/// (the authenticated account itself, cheap), healthy only when that account is in Nextcloud's <c>admin</c> group
/// (<c>data.groups</c>), which user administration and <c>user_oidc</c> pre-provisioning require.
/// </summary>
internal sealed partial class NextcloudBackendStatus(NextcloudProvisioningClient client, ILogger<NextcloudBackendStatus> logger) : IBackendStatus
{
    internal const string CurrentUserPath = "ocs/v1.php/cloud/user";
    internal const string AdminGroup = "admin";

    public async Task<BackendHealth> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            var sent = await client.SendAsync(HttpMethod.Get, CurrentUserPath, null, cancellationToken).ConfigureAwait(false);
            if (!sent.IsSuccess)
            {
                return new BackendHealth(BackendKind.Groupware, false, sent.Error.Message);
            }

            var reply = sent.Value;
            if (!reply.Succeeded)
            {
                return new BackendHealth(BackendKind.Groupware, false, reply.ToError("Nextcloud does not know the admin console's account.").Message);
            }

            // A non-admin account signs in fine but sees no other user (998), so every user would look Missing.
            return IsAdmin(reply)
                ? new BackendHealth(BackendKind.Groupware, true)
                : new BackendHealth(BackendKind.Groupware, false, $"The admin console's Nextcloud account is not in the '{AdminGroup}' group (ADR 0002 D8).");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogCheckFailed(logger, ex.GetType().Name);
            return new BackendHealth(BackendKind.Groupware, false, "The Nextcloud status check failed.");
        }
    }

    private static bool IsAdmin(OcsReply reply) =>
        reply.Data is JsonObject data
        && data["groups"] is JsonArray groups
        && groups.Any(static g => g is JsonValue v && v.GetValueKind() == JsonValueKind.String && v.GetValue<string>() == AdminGroup);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Nextcloud status check failed: {Exception}")]
    private static partial void LogCheckFailed(ILogger logger, string exception);
}

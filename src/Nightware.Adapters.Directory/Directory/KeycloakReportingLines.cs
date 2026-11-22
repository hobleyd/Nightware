using System.Globalization;

using Nightware.Adapters.Directory.Core.Admin;
using Nightware.Application.Common.Query;
using Nightware.Application.Directory;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Directory.Directory;

/// <summary>
/// <see cref="IReportingLines"/> from the user attribute <c>nightware_manager</c> (the manager's Keycloak id), which the
/// console sets, read as the <c>nightware-api</c> service account. Direct reports are found by Keycloak's attribute
/// search; like <c>/users</c>, they are enabled people only, while a manager is found even when disabled, as a former
/// colleague's card is.
/// </summary>
internal sealed class KeycloakReportingLines(IKeycloakAdminClient admin) : IReportingLines
{
    public async Task<Result<DirectoryUser>> GetManagerAsync(string userId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        if (!Guid.TryParse(userId, out _))
        {
            return NoManager();
        }

        var user = await admin.GetUserAsync(userId, cancellationToken).ConfigureAwait(false);
        if (!user.IsSuccess)
        {
            return user.Error;
        }

        if (user.Value.Attribute(KeycloakUserAttributes.Manager)?.Trim() is not { Length: > 0 } managerId || !Guid.TryParse(managerId, out _))
        {
            return NoManager();
        }

        var manager = await admin.GetUserAsync(managerId, cancellationToken).ConfigureAwait(false);
        if (!manager.IsSuccess)
        {
            // A manager deleted since is no manager.
            return manager.Error.Kind == ErrorKind.NotFound ? NoManager() : manager.Error;
        }

        return KeycloakUserMapper.IsServiceAccount(manager.Value) ? NoManager() : KeycloakUserMapper.ToDirectoryUser(manager.Value);
    }

    public async Task<Result<Page<DirectoryUser>>> ListDirectReportsAsync(string userId, ItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var offset = DirectoryPaging.Offset(query, "directReports");
        if (!offset.IsSuccess)
        {
            return offset.Error;
        }

        if (!Guid.TryParse(userId, out _))
        {
            return new Page<DirectoryUser>([]);
        }

        // One extra row says whether another page exists; the offset advances by what Keycloak returned.
        var top = DirectoryPaging.Top(query);
        var rows = await admin.FindUsersAsync(
                new KeycloakUserQuery
                {
                    Attributes = new Dictionary<string, string> { [KeycloakUserAttributes.Manager] = userId },
                    Enabled = true,
                    First = offset.Value,
                    Max = top + 1,
                    BriefRepresentation = false,
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (!rows.IsSuccess)
        {
            return rows.Error;
        }

        var window = rows.Value.Take(top).ToList();
        IReadOnlyList<DirectoryUser> reports = [.. window
            .Where(u => !KeycloakUserMapper.IsServiceAccount(u) && string.Equals(u.Attribute(KeycloakUserAttributes.Manager)?.Trim(), userId, StringComparison.OrdinalIgnoreCase))
            .Select(KeycloakUserMapper.ToDirectoryUser)];
        var next = rows.Value.Count > top ? (offset.Value + window.Count).ToString(CultureInfo.InvariantCulture) : null;
        return new Page<DirectoryUser>(reports, next);
    }

    private static Error NoManager() => Error.NotFound("The user has no manager.");
}

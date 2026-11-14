using System.Globalization;

using Nightware.Adapters.Directory.Core.Admin;
using Nightware.Application.Common.Query;
using Nightware.Application.Directory;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Directory.Directory;

/// <summary>
/// <see cref="IUserDirectory"/> over the Keycloak admin API, as the <c>nightware-api</c> service account
/// (ADR 0001 §17). Lists enabled people only (service-account users are not people); a lookup by name
/// finds disabled users too, so a former colleague's card still resolves.
/// </summary>
/// <remarks>
/// Paging is Keycloak's <c>first</c>/<c>max</c>; the <c>$skiptoken</c> is the next offset. One extra row is
/// requested to know whether another page exists, and the offset advances by what Keycloak returned
/// (not by what survived filtering), so a filtered row can never shift a person off a page boundary.
/// </remarks>
internal sealed class KeycloakUserDirectory(IKeycloakAdminClient admin) : IUserDirectory
{
    /// <summary>Graph's default <c>/users</c> page size.</summary>
    public const int DefaultPageSize = 100;

    public async Task<Result<DirectoryUser>> GetAsync(string idOrUpn, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idOrUpn);
        var key = idOrUpn.Trim();
        Result<KeycloakUser> found;
        if (key.Contains('@', StringComparison.Ordinal))
        {
            // A UPN may be a username that looks like an address rather than the email.
            found = await admin.FindUserByEmailAsync(key, cancellationToken).ConfigureAwait(false);
            if (!found.IsSuccess && found.Error.Kind == ErrorKind.NotFound)
            {
                found = await admin.FindUserByUsernameAsync(key, cancellationToken).ConfigureAwait(false);
            }
        }
        else if (Guid.TryParse(key, out _))
        {
            found = await admin.GetUserAsync(key, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            found = await admin.FindUserByUsernameAsync(key, cancellationToken).ConfigureAwait(false);
        }

        if (!found.IsSuccess)
        {
            return found.Error;
        }

        return KeycloakUserMapper.IsServiceAccount(found.Value)
            ? Error.NotFound("The user was not found in the directory.")
            : KeycloakUserMapper.ToDirectoryUser(found.Value);
    }

    public async Task<Result<Page<DirectoryUser>>> ListAsync(ItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (Unsupported(query) is { } option)
        {
            return Error.Validation($"{option} is not supported on users.", option);
        }

        var offset = 0;
        if (query.SkipToken is { } token && !int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out offset))
        {
            return Error.Validation("The $skiptoken is not valid.", "$skiptoken");
        }

        var top = query.Top ?? DefaultPageSize;
        var rows = await admin.FindUsersAsync(
                new KeycloakUserQuery { First = offset, Max = top + 1, Enabled = true, BriefRepresentation = false },
                cancellationToken)
            .ConfigureAwait(false);
        if (!rows.IsSuccess)
        {
            return rows.Error;
        }

        var window = rows.Value.Take(top).ToList();
        IReadOnlyList<DirectoryUser> users = [.. window
            .Where(static u => !KeycloakUserMapper.IsServiceAccount(u))
            .Select(KeycloakUserMapper.ToDirectoryUser)];
        var next = rows.Value.Count > top ? (offset + window.Count).ToString(CultureInfo.InvariantCulture) : null;
        return new Page<DirectoryUser>(users, next);
    }

    private static string? Unsupported(ItemQuery query) =>
        query.Filter is not null ? "$filter"
        : query.Search is not null ? "$search"
        : query.OrderBy.Count > 0 ? "$orderby"
        : query.Skip is not null ? "$skip"
        : query.Expand.Count > 0 ? "$expand"
        : null;
}

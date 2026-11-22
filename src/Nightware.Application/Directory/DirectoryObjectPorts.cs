using Nightware.Application.Common.Query;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Directory;

/// <summary>
/// The groups of the signed-in user's organizations (phase 20). Only the caller's organizations are visible: a group
/// elsewhere is NotFound, and another user's groups are only those in an organization the caller shares.
/// </summary>
public interface IGroupDirectory
{
    Task<Result<Page<DirectoryGroup>>> ListAsync(ItemQuery query, CancellationToken cancellationToken);

    /// <summary>NotFound when no organization of the caller's has the group.</summary>
    Task<Result<DirectoryGroup>> GetAsync(string groupId, CancellationToken cancellationToken);

    /// <summary>The group's members (people with access, not disabled accounts); NotFound as <see cref="GetAsync"/>.</summary>
    Task<Result<Page<DirectoryUser>>> ListMembersAsync(string groupId, ItemQuery query, CancellationToken cancellationToken);

    /// <summary>The groups <paramref name="userId"/> (a directory id) belongs to, in the caller's organizations.</summary>
    Task<Result<Page<DirectoryGroup>>> ListMemberOfAsync(string userId, ItemQuery query, CancellationToken cancellationToken);
}

/// <summary>Who reports to whom (phase 20), as the console records it.</summary>
public interface IReportingLines
{
    /// <summary>The manager of <paramref name="userId"/> (a directory id); NotFound when they have none.</summary>
    Task<Result<DirectoryUser>> GetManagerAsync(string userId, CancellationToken cancellationToken);

    /// <summary>The enabled people whose manager is <paramref name="userId"/>.</summary>
    Task<Result<Page<DirectoryUser>>> ListDirectReportsAsync(string userId, ItemQuery query, CancellationToken cancellationToken);
}

/// <summary>The organizations (entities) the signed-in user belongs to (phase 20).</summary>
public interface IOrganizationDirectory
{
    Task<Result<IReadOnlyList<DirectoryOrganization>>> ListAsync(CancellationToken cancellationToken);
}

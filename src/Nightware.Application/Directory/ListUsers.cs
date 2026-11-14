using Nightware.Application.Common.Query;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Directory;

/// <summary>
/// <c>GET /users</c>: one page of the whole directory (the client's daily contact-cache walk). The
/// directory decides what of <see cref="ItemQuery"/> it can honour; the contract sends only
/// <c>$select</c>, <c>$top</c> and the <c>$skiptoken</c> of a previous page.
/// </summary>
public sealed class ListUsers(IUserDirectory directory)
{
    public Task<Result<Page<DirectoryUser>>> ExecuteAsync(ItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return directory.ListAsync(query, cancellationToken);
    }
}

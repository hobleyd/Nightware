using Nightware.Domain.Files;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Files;

/// <summary>The item a sharing link names (<c>GET /shares/{id}/driveItem</c>).</summary>
public sealed class GetSharedItem(ISharedFileReader reader)
{
    public Task<Result<SharedItem>> ExecuteAsync(ShareLink link, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(link);
        return reader.GetItemAsync(link, cancellationToken);
    }
}

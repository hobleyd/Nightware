using Nightware.Domain.Files;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Files;

/// <summary>
/// The first leg of <c>GET /shares/{id}/driveItem/content[?format=pdf]</c>: confirms the caller can open the
/// item and that it can be served in <c>format</c>, before the API hands out a download URL. Downloading
/// and converting happen on the second leg (<see cref="DownloadSharedItem"/>), whose client allows minutes
/// where this one allows seconds.
/// </summary>
public sealed class PrepareSharedItemDownload(ISharedFileReader reader, IDocumentConverter converter)
{
    public async Task<Result<SharedItem>> ExecuteAsync(ShareLink link, ContentFormat format, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(link);
        var item = await reader.GetItemAsync(link, cancellationToken).ConfigureAwait(false);
        if (!item.IsSuccess)
        {
            return item;
        }

        var allowed = SharedItemDownloads.Check(item.Value, format, converter);
        return allowed.IsSuccess ? item : allowed.Error;
    }
}

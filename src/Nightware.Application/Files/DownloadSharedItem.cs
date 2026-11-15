using Nightware.Domain.Files;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Files;

/// <summary>
/// A shared item's bytes — as stored, or rendered to PDF by <see cref="IDocumentConverter"/> when
/// <see cref="ContentFormat.Pdf"/> is asked for and the item is not one already.
/// </summary>
public sealed class DownloadSharedItem(ISharedFileReader reader, IDocumentConverter converter)
{
    public async Task<Result<BlobContent>> ExecuteAsync(ShareLink link, ContentFormat format, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(link);

        // Checked again rather than trusted from the first leg: the URL outlives that request.
        var item = await reader.GetItemAsync(link, cancellationToken).ConfigureAwait(false);
        if (!item.IsSuccess)
        {
            return item.Error;
        }

        var allowed = SharedItemDownloads.Check(item.Value, format, converter);
        if (!allowed.IsSuccess)
        {
            return allowed.Error;
        }

        var content = await reader.GetContentAsync(link, cancellationToken).ConfigureAwait(false);
        if (!content.IsSuccess || !SharedItemDownloads.NeedsConversion(item.Value, format))
        {
            return content.IsSuccess ? content.Value with { FileName = content.Value.FileName ?? item.Value.Name } : content;
        }

        return await converter.ConvertToPdfAsync(content.Value, item.Value.Name, cancellationToken).ConfigureAwait(false);
    }
}

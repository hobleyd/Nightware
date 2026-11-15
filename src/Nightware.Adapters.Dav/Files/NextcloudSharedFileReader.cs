using System.Globalization;

using Nightware.Adapters.Dav.Core;
using Nightware.Application.Files;
using Nightware.Domain.Files;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Files;

/// <summary>
/// <see cref="ISharedFileReader"/> over Nextcloud: public shares through public-share WebDAV, internal
/// <c>/f/</c> links through a file-id search in the caller's Files (streamed with the caller's credential).
/// </summary>
/// <remarks>
/// Only <see cref="ShareLink.SitePath"/> is read. The link's host was matched to an advertised file site by
/// the API, and every request goes to the configured <c>Dav:BaseUrl</c> — never to the link itself.
/// </remarks>
internal sealed class NextcloudSharedFileReader(NextcloudPublicShares shares, NextcloudFileIdSearch search, IDavClient dav) : ISharedFileReader
{
    public async Task<Result<SharedItem>> GetItemAsync(ShareLink link, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(link);
        switch (Parse(link))
        {
            case NextcloudLink.PublicShare share:
                return await shares.GetItemAsync(share.Token, link, cancellationToken).ConfigureAwait(false);
            case NextcloudLink.InternalFile file:
                var found = await search.FindAsync(file.FileId, cancellationToken).ConfigureAwait(false);
                return found.IsSuccess
                    ? SharedItemMapping.ToItem(found.Value, link, file.FileId.ToString(CultureInfo.InvariantCulture))
                    : found.Error;
            default:
                return NotADocumentLink();
        }
    }

    public async Task<Result<BlobContent>> GetContentAsync(ShareLink link, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(link);
        switch (Parse(link))
        {
            case NextcloudLink.PublicShare share:
                return await shares.GetContentAsync(share.Token, cancellationToken).ConfigureAwait(false);
            case NextcloudLink.InternalFile file:
                var found = await search.FindAsync(file.FileId, cancellationToken).ConfigureAwait(false);
                if (!found.IsSuccess)
                {
                    return found.Error;
                }

                var download = await dav.OpenReadAsync(found.Value.Href, cancellationToken).ConfigureAwait(false);
                if (!download.IsSuccess)
                {
                    return download.Error;
                }

                Stream body = new ResponseStream(download.Value.Content, download.Value);
                var type = found.Value.Text(DavNames.GetContentType) ?? download.Value.ContentType;
                return new BlobContent(type, download.Value.Length, _ => Task.FromResult(body));
            default:
                return NotADocumentLink();
        }
    }

    private static NextcloudLink? Parse(ShareLink link) => NextcloudLink.TryParse(link.SitePath, out var parsed) ? parsed : null;

    private static Error NotADocumentLink() => Error.NotFound("The link is not a Nextcloud share or file link.");
}

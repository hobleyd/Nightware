using Nightware.Application.Files;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Office;

/// <summary>
/// The converter when no Collabora is configured: nothing is convertible, so a request for a PDF is refused
/// before any download and NightMail falls back to the original file.
/// </summary>
internal sealed class UnconfiguredDocumentConverter : IDocumentConverter
{
    public bool CanConvertToPdf(string fileName, string? mimeType) => false;

    public async Task<Result<BlobContent>> ConvertToPdfAsync(BlobContent source, string fileName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        // The port takes ownership of the source; release its backend response.
        await (await source.Open(cancellationToken).ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false);
        return Error.Unsupported("Document conversion is not configured on this server (Collabora:BaseUrl).");
    }
}

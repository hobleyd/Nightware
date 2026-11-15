using Nightware.Domain.Kernel;

namespace Nightware.Application.Files;

/// <summary>
/// Server-side rendering of Office documents to PDF — what Graph's <c>content?format=pdf</c> does for
/// SharePoint/OneDrive — so the client can preview a document it has no viewer for.
/// </summary>
public interface IDocumentConverter
{
    /// <summary>
    /// Whether <see cref="ConvertToPdfAsync"/> accepts a file of this name and type. False whenever no
    /// converter is configured, so a request for a PDF fails before anything is downloaded.
    /// </summary>
    bool CanConvertToPdf(string fileName, string? mimeType);

    /// <summary>
    /// Renders <paramref name="source"/> as PDF. Takes ownership of <paramref name="source"/>: its stream is
    /// opened and disposed here whether or not the conversion succeeds. A document the converter cannot read is
    /// <see cref="ErrorKind.Validation"/>; an unreachable or failing converter is
    /// <see cref="ErrorKind.BackendUnavailable"/>.
    /// </summary>
    Task<Result<BlobContent>> ConvertToPdfAsync(BlobContent source, string fileName, CancellationToken cancellationToken);
}

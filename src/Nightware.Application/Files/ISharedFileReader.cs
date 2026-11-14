using Nightware.Domain.Files;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Files;

public interface ISharedFileReader
{
    Task<Result<SharedItem>> GetItemAsync(ShareLink link, CancellationToken cancellationToken);

    /// <summary><see cref="ContentFormat.Pdf"/> yields <see cref="ErrorKind.Unsupported"/> in v1 (ADR 0001 §22).</summary>
    Task<Result<BlobContent>> GetContentAsync(ShareLink link, ContentFormat format, CancellationToken cancellationToken);
}

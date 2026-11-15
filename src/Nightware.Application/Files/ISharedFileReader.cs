using Nightware.Domain.Files;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Files;

/// <summary>
/// Reads documents reached through sharing links in mail (Graph <c>/shares/{id}/driveItem</c>), as the
/// current caller. The link's host has already been matched to one of this server's file sites; the
/// implementation interprets only <see cref="ShareLink.SitePath"/>.
/// </summary>
public interface ISharedFileReader
{
    /// <summary>
    /// The item the link names. A link this backend does not recognise, or one to nothing, is
    /// <see cref="ErrorKind.NotFound"/>; one the caller may not open (including a password-protected
    /// share) is <see cref="ErrorKind.Forbidden"/> — never an authentication failure, which the client
    /// would read as "sign in again".
    /// </summary>
    Task<Result<SharedItem>> GetItemAsync(ShareLink link, CancellationToken cancellationToken);

    /// <summary>The file's bytes as stored. The returned stream holds a backend response open: open and dispose it.</summary>
    Task<Result<BlobContent>> GetContentAsync(ShareLink link, CancellationToken cancellationToken);
}

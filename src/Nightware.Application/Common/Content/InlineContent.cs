using Nightware.Domain.Kernel;

namespace Nightware.Application.Common.Content;

/// <summary>
/// Reads a file into memory for a response that carries it inline (an attachment's <c>contentBytes</c>), up to
/// <see cref="MaxBytes"/>. A larger file is <see cref="ErrorKind.PayloadTooLarge"/> (413); a mail or To Do
/// attachment's <c>/$value</c> streams it instead. Without the cap one request could buffer a file of any size, three
/// times over (bytes, base64, JSON).
/// </summary>
public static class InlineContent
{
    /// <summary>The largest attachment the façade lets a client create (a mail attachment, Stalwart's upload limit).</summary>
    public const long MaxBytes = 50_000_000;

    public static Error TooLarge() =>
        new(ErrorKind.PayloadTooLarge, "requestEntityTooLarge", $"The attachment is larger than {MaxBytes} bytes, the most returned inline; read it from its /$value where there is one.");

    /// <summary>The stream's bytes, or <see cref="TooLarge"/> once more than <see cref="MaxBytes"/> arrive.</summary>
    public static Task<Result<byte[]>> ReadAsync(Stream stream, CancellationToken cancellationToken) => ReadAsync(stream, MaxBytes, cancellationToken);

    /// <summary>The stream's bytes, or <see cref="TooLarge"/> as soon as more than <paramref name="maxBytes"/> arrive.</summary>
    public static async Task<Result<byte[]>> ReadAsync(Stream stream, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return TooLarge();
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}

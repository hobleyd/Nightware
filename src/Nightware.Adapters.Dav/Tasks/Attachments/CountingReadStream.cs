namespace Nightware.Adapters.Dav.Tasks.Attachments;

/// <summary>
/// A forward-only view of a request body that counts what it passes on and, given a limit, ends
/// the stream there — recording whether more bytes were waiting — so an oversized upload is never
/// streamed into Nextcloud whole.
/// </summary>
internal sealed class CountingReadStream(Stream inner, long? limit) : Stream
{
    public long BytesRead { get; private set; }

    /// <summary>True when the body held more than the limit.</summary>
    public bool Exceeded { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => BytesRead;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (Exceeded || buffer.Length == 0)
        {
            return 0;
        }

        if (limit is { } max && BytesRead >= max)
        {
            // At the limit: one probe byte tells a complete body from an oversized one.
            var probe = new byte[1];
            Exceeded = await inner.ReadAsync(probe, cancellationToken).ConfigureAwait(false) > 0;
            return 0;
        }

        var allowed = limit is { } cap ? (int)Math.Min(buffer.Length, cap - BytesRead) : buffer.Length;
        var read = await inner.ReadAsync(buffer[..allowed], cancellationToken).ConfigureAwait(false);
        BytesRead += read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

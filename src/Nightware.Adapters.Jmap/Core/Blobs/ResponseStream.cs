namespace Nightware.Adapters.Jmap.Core.Blobs;

/// <summary>A read-only response body stream that disposes its <see cref="HttpResponseMessage"/> with it.</summary>
internal sealed class ResponseStream : Stream
{
    private readonly HttpResponseMessage _response;
    private readonly Stream _inner;

    private ResponseStream(HttpResponseMessage response, Stream inner)
    {
        _response = response;
        _inner = inner;
    }

    public static async Task<Stream> OpenAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        try
        {
            var inner = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return new ResponseStream(response, inner);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => _response.Content.Headers.ContentLength ?? throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => _inner.Read(buffer);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(buffer, cancellationToken);

    public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken) =>
        _inner.CopyToAsync(destination, bufferSize, cancellationToken);

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
            _response.Dispose();
        }

        base.Dispose(disposing);
    }
}

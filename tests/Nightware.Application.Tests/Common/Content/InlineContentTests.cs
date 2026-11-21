using Nightware.Application.Common.Content;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Common.Content;

public sealed class InlineContentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_file_within_the_cap_is_read_whole()
    {
        var bytes = Enumerable.Range(0, 200_000).Select(static i => (byte)i).ToArray();

        (await InlineContent.ReadAsync(new MemoryStream(bytes), 200_000, Ct)).Value.ShouldBe(bytes);
    }

    [Fact]
    public async Task A_file_over_the_cap_is_too_large_without_reading_the_rest()
    {
        var stream = new CountingStream(new MemoryStream(new byte[1_000_000]));

        var result = await InlineContent.ReadAsync(stream, 100_000, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.PayloadTooLarge);
        result.Error.Code.ShouldBe("requestEntityTooLarge");
        stream.BytesRead.ShouldBeLessThan(200_000);
    }

    [Fact]
    public void The_cap_is_the_largest_attachment_a_client_can_create() =>
        InlineContent.MaxBytes.ShouldBe(Nightware.Application.Mail.Write.DraftUploadLimits.MaxBytes);

    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => inner.Length;

        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

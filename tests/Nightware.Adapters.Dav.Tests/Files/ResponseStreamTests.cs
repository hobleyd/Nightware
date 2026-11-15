using Nightware.Adapters.Dav.Files;

namespace Nightware.Adapters.Dav.Tests.Files;

public sealed class ResponseStreamTests
{
    [Fact]
    public async Task Reads_the_content_and_releases_its_owner_on_dispose()
    {
        var owner = Substitute.For<IDisposable>();
        var stream = new ResponseStream(new MemoryStream([1, 2, 3]), owner);
        var buffer = new byte[3];

        (await stream.ReadAsync(buffer, TestContext.Current.CancellationToken)).ShouldBe(3);
        stream.Read(new byte[1], 0, 1).ShouldBe(0);
        stream.CanRead.ShouldBeTrue();
        stream.CanSeek.ShouldBeFalse();
        stream.CanWrite.ShouldBeFalse();
        Should.Throw<NotSupportedException>(() => stream.Length);
        Should.Throw<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Should.Throw<NotSupportedException>(() => stream.Write([1], 0, 1));
        await stream.DisposeAsync();

        owner.Received(1).Dispose();
        buffer.ShouldBe(new byte[] { 1, 2, 3 });
    }
}

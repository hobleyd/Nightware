using Nightware.Application.Directory;
using Nightware.Conformance.Tests.Harness;
using Nightware.Domain.Kernel;

namespace Nightware.Conformance.Tests.Arrangements;

/// <summary>Phase 19: the signed-in user's photo, photo metadata, and setting or removing one's own.</summary>
public sealed class PhotoArrangements : IConformanceArrangements
{
    private static readonly MailboxRef Me = new("me@example.com");

    /// <summary>The fixtures' 8x8 JPEG.</summary>
    private static readonly byte[] Jpeg = Convert.FromBase64String("/9j/4AAQSkZJRgABAQAASABIAAD/4QBMRXhpZgAATU0AKgAAAAgAAYdpAAQAAAABAAAAGgAAAAAAA6ABAAMAAAABAAEAAKACAAQAAAABAAAACKADAAQAAAABAAAACAAAAAD/7QA4UGhvdG9zaG9wIDMuMAA4QklNBAQAAAAAAAA4QklNBCUAAAAAABDUHYzZjwCyBOmACZjs+EJ+/8AAEQgACAAIAwEiAAIRAQMRAf/EAB8AAAEFAQEBAQEBAAAAAAAAAAABAgMEBQYHCAkKC//EALUQAAIBAwMCBAMFBQQEAAABfQECAwAEEQUSITFBBhNRYQcicRQygZGhCCNCscEVUtHwJDNicoIJChYXGBkaJSYnKCkqNDU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6g4SFhoeIiYqSk5SVlpeYmZqio6Slpqeoqaqys7S1tre4ubrCw8TFxsfIycrS09TV1tfY2drh4uPk5ebn6Onq8fLz9PX29/j5+v/EAB8BAAMBAQEBAQEBAQEAAAAAAAABAgMEBQYHCAkKC//EALURAAIBAgQEAwQHBQQEAAECdwABAgMRBAUhMQYSQVEHYXETIjKBCBRCkaGxwQkjM1LwFWJy0QoWJDThJfEXGBkaJicoKSo1Njc4OTpDREVGR0hJSlNUVVZXWFlaY2RlZmdoaWpzdHV2d3h5eoKDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uLj5OXm5+jp6vLz9PX29/j5+v/bAEMABAQEBAQEBgQEBgkGBgYJDAkJCQkMDwwMDAwMDxIPDw8PDw8SEhISEhISEhUVFRUVFRkZGRkZHBwcHBwcHBwcHP/bAEMBBAUFBwcHDAcHDB0UEBQdHR0dHR0dHR0dHR0dHR0dHR0dHR0dHR0dHR0dHR0dHR0dHR0dHR0dHR0dHR0dHR0dHf/dAAQAAf/aAAwDAQACEQMRAD8A8looor9WPgT/2Q==");

    private static BlobContent Photo() => new("image/jpeg", Jpeg.Length, _ => Task.FromResult<Stream>(new MemoryStream(Jpeg)));

    private static void PhotoOf(PortFakes fakes, string address) =>
        fakes.For<IUserPhotoReader>().GetAsync(address, Arg.Any<CancellationToken>()).Returns(_ => Photo());

    private static void Writes(PortFakes fakes)
    {
        fakes.For<IUserPhotoWriter>().SetAsync(Me, Arg.Is<ReadOnlyMemory<byte>>(m => m.Length == Jpeg.Length), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        fakes.For<IUserPhotoWriter>().DeleteAsync(Me, Arg.Any<CancellationToken>()).Returns(Result.Success());
    }

    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        yield return new ConformanceArrangement("directory.me.photo-value", static fakes => PhotoOf(fakes, "me@example.com"));
        yield return new ConformanceArrangement("directory.me.photo.get", static fakes => PhotoOf(fakes, "me@example.com"));
        yield return new ConformanceArrangement("directory.users.photo.get", static fakes => PhotoOf(fakes, "sam@example.com"));
        foreach (var id in new[] { "directory.me.photo.put", "directory.me.photo.patch", "directory.users.photo.put", "directory.users.photo.patch", "directory.me.photo.delete", "directory.users.photo.delete" })
        {
            yield return new ConformanceArrangement(id, Writes);
        }
    }
}

using System.Text;
using Nightware.Application.Calendar.Calendars;
using Nightware.Conformance.Tests.Harness;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Conformance.Tests.Arrangements;

/// <summary>
/// Phase 13 event attachments (ADR 0006 D6), against captures from Microsoft 365, compared on the members both sides
/// emit: Exchange adds <c>@odata.mediaContentType</c>, and lists bytes the façade sends only on a single get.
/// </summary>
public sealed class EventAttachmentArrangements : IConformanceArrangements
{
    private static readonly MailboxRef Me = new("me@example.com");
    private static readonly EventId Event = new("AAMk-id-0010=");
    private static readonly AttachmentId Note = new("AAMk-id-0011=");

    private static readonly EventAttachment Stored = new(Note, "note.txt", "text/plain", 175)
    {
        LastModifiedDateTime = new DateTimeOffset(2026, 11, 20, 22, 38, 46, TimeSpan.Zero),
    };

    private static readonly EventAttachment WithContent = Stored with { Content = Encoding.UTF8.GetBytes("Nightware capture") };

    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        yield return new ConformanceArrangement("calendar.events.attachments.create", static fakes =>
            fakes.For<IEventAttachmentStore>().AddAsync(Me, Event, "note.txt", "text/plain", Arg.Is<ReadOnlyMemory<byte>>(c => c.Length == 17), Arg.Any<CancellationToken>())
                .Returns(WithContent))
        {
            Normalise = ParityNormalisers.SharedMembers,
        };

        yield return new ConformanceArrangement("calendar.events.attachments.list", static fakes =>
            fakes.For<IEventAttachmentStore>().ListAsync(Me, Event, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<EventAttachment>>.Success([Stored])))
        {
            Normalise = ParityNormalisers.SharedMembers,
        };

        yield return new ConformanceArrangement("calendar.events.attachments.get", static fakes =>
            fakes.For<IEventAttachmentStore>().GetAsync(Me, Event, Note, Arg.Any<CancellationToken>()).Returns(WithContent))
        {
            Normalise = ParityNormalisers.SharedMembers,
        };

        yield return new ConformanceArrangement("calendar.events.attachments.delete", static fakes =>
            fakes.For<IEventAttachmentStore>().DeleteAsync(Me, Event, Note, Arg.Any<CancellationToken>()).Returns(Result.Success()));
    }
}

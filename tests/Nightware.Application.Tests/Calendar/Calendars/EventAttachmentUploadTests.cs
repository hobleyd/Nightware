using Nightware.Application.Calendar.Calendars;
using Nightware.Application.Mail.Write;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Calendar.Calendars;

public sealed class EventAttachmentUploadTests
{
    private static readonly MailboxRef Me = new("me@example.com");
    private static readonly EventId Event = new("ev1");

    private readonly IEventAttachmentUploads _uploads = Substitute.For<IEventAttachmentUploads>();
    private readonly IEventAttachmentStore _store = Substitute.For<IEventAttachmentStore>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Begin_trims_the_name_and_defaults_the_type()
    {
        var state = new EventUploadState("s1", new EventUploadFile("big.bin", "application/octet-stream", 10), 0, DateTimeOffset.UnixEpoch);
        _uploads.BeginAsync(Me, Event, new EventUploadFile("big.bin", "application/octet-stream", 10), Arg.Any<CancellationToken>()).Returns(state);

        (await new BeginEventAttachmentUpload(_uploads).ExecuteAsync(Me, Event, "  big.bin ", " ", 10, Ct)).Value.ShouldBe(state);
    }

    [Theory]
    [InlineData(null, 10L, ErrorKind.Validation)]
    [InlineData("a", 0L, ErrorKind.Validation)]
    [InlineData("a", EventUploadLimits.MaxBytes + 1, ErrorKind.PayloadTooLarge)]
    public async Task Begin_refuses_a_file_out_of_limits(string? name, long size, ErrorKind kind)
    {
        (await new BeginEventAttachmentUpload(_uploads).ExecuteAsync(Me, Event, name, null, size, Ct)).Error!.Kind.ShouldBe(kind);
        _uploads.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_chunk_passes_its_offset_and_length()
    {
        using var body = new MemoryStream([1, 2, 3]);
        _uploads.AppendAsync(Me, Event, "s1", 4, 3, 10, body, Arg.Any<CancellationToken>()).Returns(new EventUploadProgress(7, null));

        (await new UploadEventAttachmentChunk(_uploads).ExecuteAsync(Me, Event, "s1", new ContentRangeHeader(4, 6, 10), body, Ct)).Value.NextExpected.ShouldBe(7);
    }

    [Fact]
    public async Task A_bad_range_or_an_oversize_chunk_never_reaches_the_store()
    {
        using var body = new MemoryStream();
        var useCase = new UploadEventAttachmentChunk(_uploads);

        (await useCase.ExecuteAsync(Me, Event, "s1", new ContentRangeHeader(5, 4, 10), body, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await useCase.ExecuteAsync(Me, Event, "s1", new ContentRangeHeader(0, 9, 9), body, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await useCase.ExecuteAsync(Me, Event, "s1", new ContentRangeHeader(0, EventUploadLimits.MaxChunkBytes, EventUploadLimits.MaxBytes), body, Ct))
            .Error!.Kind.ShouldBe(ErrorKind.PayloadTooLarge);
        _uploads.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Content_passes_through_to_the_store()
    {
        var blob = new BlobContent("text/plain", 1, _ => Task.FromResult<Stream>(new MemoryStream([1])));
        _store.GetContentAsync(Me, Event, new AttachmentId("a1"), Arg.Any<CancellationToken>()).Returns(blob);

        (await new EventAttachmentsUseCase(_store).GetContentAsync(Me, Event, new AttachmentId("a1"), Ct)).Value.ShouldBe(blob);
    }
}

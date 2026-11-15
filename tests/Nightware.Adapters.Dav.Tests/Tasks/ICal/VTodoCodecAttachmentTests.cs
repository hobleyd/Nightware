using Microsoft.Extensions.Time.Testing;
using Nightware.Adapters.Dav.Tasks.ICal;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Tests.Tasks.ICal;

public sealed class VTodoCodecAttachmentTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 10, 0, 0, TimeSpan.Zero);
    private readonly VTodoCodec _codec = new(new FakeTimeProvider(Now));

    private const string Task = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:x\r\nBEGIN:VTODO\r\nUID:1\r\nSUMMARY:Task\r\nX-KEEP:me\r\n"
        + "ATTACH;FMTTYPE=application/pdf;FILENAME=Report.pdf;SIZE=1234:https://cloud.example.com/remote.php/dav/files/alice/Report.pdf\r\n"
        + "ATTACH:https://cloud.example.com/remote.php/dav/files/alice/Plain%20name.txt\r\n"
        + "ATTACH;ENCODING=BASE64;VALUE=BINARY:SGVsbG8=\r\n"
        + "END:VTODO\r\nEND:VCALENDAR\r\n";

    [Fact]
    public void Reads_uri_attachments_with_their_parameters()
    {
        var links = _codec.ReadAttachments(Task).Value;

        links.Count.ShouldBe(2);
        links[0].ShouldBe(new TodoAttachmentLink(new Uri("https://cloud.example.com/remote.php/dav/files/alice/Report.pdf"), "Report.pdf", "application/pdf") { Size = 1234 });
        links[1].FileName.ShouldBe("Plain name.txt");
        links[1].ContentType.ShouldBe("application/octet-stream");
        links[1].Size.ShouldBeNull();
    }

    [Fact]
    public void Adds_an_attachment_and_keeps_the_rest()
    {
        var link = new TodoAttachmentLink(new Uri("https://cloud.example.com/remote.php/dav/files/alice/N/a%20b.eml"), "a b.eml", "message/rfc822") { Size = 99 };

        var ics = _codec.AddAttachment(Task, link).Value;

        ics.ShouldContain("X-KEEP:me");
        var links = _codec.ReadAttachments(ics).Value;
        links.Count.ShouldBe(3);
        links.ShouldContain(link);
        _codec.Parse(ics).Value.Single().Task.LastModifiedDateTime.ShouldBe(Now);
    }

    [Fact]
    public void A_resource_without_a_task_has_no_attachments()
    {
        const string Empty = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:x\r\nEND:VCALENDAR\r\n";

        _codec.ReadAttachments(Empty).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _codec.AddAttachment(Empty, new TodoAttachmentLink(new Uri("https://x/y"), "y", "text/plain")).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public void Garbage_is_a_validation_error() =>
        _codec.ReadAttachments("not ical").IsSuccess.ShouldBeFalse();
}

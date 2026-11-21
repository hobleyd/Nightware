using MimeKit;

using Nightware.Adapters.Jmap.Mail.Write;
using Nightware.Application.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Tests.Mail.Write;

public sealed class JmapItipMailTests
{
    [Fact]
    public void The_reply_is_imip_text_beside_a_calendar_part_with_its_method()
    {
        var message = new ItipMessage("dana@contoso.example", "Declined: Review", "Away that week", "BEGIN:VCALENDAR\r\nMETHOD:REPLY\r\nEND:VCALENDAR\r\n", "REPLY");

        var mime = JmapItipMail.Build(message, new EmailAddress("bob@acme.example", "Bob"), "<x@acme.example>", new DateTimeOffset(2026, 11, 21, 9, 0, 0, TimeSpan.Zero));

        mime.From.Mailboxes.Single().Address.ShouldBe("bob@acme.example");
        mime.To.Mailboxes.Single().Address.ShouldBe("dana@contoso.example");
        mime.Subject.ShouldBe("Declined: Review");
        var parts = mime.Body.ShouldBeOfType<MultipartAlternative>().OfType<TextPart>().ToList();
        parts[0].ContentType.MimeType.ShouldBe("text/plain");
        parts[0].Text.ShouldBe("Away that week");
        parts[1].ContentType.MimeType.ShouldBe("text/calendar");
        parts[1].ContentType.Parameters["method"].ShouldBe("REPLY");
        parts[1].Text.ShouldContain("METHOD:REPLY");
    }
}

namespace Nightware.Integration.Tests.Mail.Read;

/// <summary>Raw MIME for seeding. Every subject carries a unique tag so parallel runs never collide.</summary>
internal static class TestMessages
{
    public static string Tag() => "nw-p2-" + Guid.NewGuid().ToString("N")[..12];

    public static string Plain(string subject) =>
        $"From: Bob Brown <bob@nightware.localhost>\r\nTo: Alice Anderson <alice@nightware.localhost>\r\nSubject: {subject}\r\n"
        + $"Date: {DateTimeOffset.UtcNow:R}\r\nMessage-ID: <{Guid.NewGuid():N}@nightware.localhost>\r\nMIME-Version: 1.0\r\n"
        + "Content-Type: text/plain; charset=utf-8\r\n\r\nHello Alice.\r\n";

    /// <summary>HTML with an inline image, a PDF and an attached message (a Graph itemAttachment).</summary>
    public static string WithAttachments(string subject) =>
        $"From: Bob Brown <bob@nightware.localhost>\r\nTo: Alice Anderson <alice@nightware.localhost>\r\nSubject: {subject}\r\n"
        + $"Date: {DateTimeOffset.UtcNow:R}\r\nMessage-ID: <{Guid.NewGuid():N}@nightware.localhost>\r\nImportance: high\r\nMIME-Version: 1.0\r\n"
        + "Content-Type: multipart/mixed; boundary=\"outer\"\r\n\r\n"
        + "--outer\r\nContent-Type: multipart/related; boundary=\"rel\"\r\n\r\n"
        + "--rel\r\nContent-Type: text/html; charset=utf-8\r\n\r\n<p>See the <b>numbers</b>.</p><img src=\"cid:logo@nw\">\r\n"
        + "--rel\r\nContent-Type: image/png\r\nContent-ID: <logo@nw>\r\nContent-Disposition: inline; filename=logo.png\r\nContent-Transfer-Encoding: base64\r\n\r\n"
        + "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==\r\n"
        + "--rel--\r\n"
        + "--outer\r\nContent-Type: application/pdf; name=\"q2.pdf\"\r\nContent-Disposition: attachment; filename=\"q2.pdf\"\r\nContent-Transfer-Encoding: base64\r\n\r\n"
        + "JVBERi0xLjQK\r\n"
        + "--outer\r\nContent-Type: message/rfc822\r\nContent-Disposition: attachment\r\n\r\n"
        + "From: Carol <carol@example.com>\r\nSubject: Inner note\r\n\r\nInner body.\r\n"
        + "--outer--\r\n";

    /// <summary>An iMIP invitation as Nextcloud sends it: multipart/alternative carrying text/calendar; method=REQUEST.</summary>
    public static string Invitation(string subject) =>
        $"From: Bob Brown <bob@nightware.localhost>\r\nTo: Alice Anderson <alice@nightware.localhost>\r\nSubject: {subject}\r\n"
        + $"Date: {DateTimeOffset.UtcNow:R}\r\nMessage-ID: <{Guid.NewGuid():N}@nightware.localhost>\r\nMIME-Version: 1.0\r\n"
        + "Content-Type: multipart/alternative; boundary=\"alt\"\r\n\r\n"
        + "--alt\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nYou are invited.\r\n"
        + "--alt\r\nContent-Type: text/calendar; method=REQUEST; charset=utf-8\r\n\r\n"
        + "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//Nightware tests//EN\r\nMETHOD:REQUEST\r\nBEGIN:VEVENT\r\n"
        + $"UID:{subject}@nightware.localhost\r\nDTSTAMP:20261115T000000Z\r\nDTSTART:20261201T090000Z\r\nDTEND:20261201T100000Z\r\n"
        + "SUMMARY:Planning\r\nLOCATION:Boardroom\r\nORGANIZER;CN=Bob Brown:mailto:bob@nightware.localhost\r\n"
        + "ATTENDEE;PARTSTAT=NEEDS-ACTION;RSVP=TRUE:mailto:alice@nightware.localhost\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n"
        + "--alt--\r\n";

    /// <summary>A message alice sent to <paramref name="recipient"/> (as filed in Sent Items).</summary>
    public static string Sent(string subject, string recipient) =>
        $"From: Alice Anderson <alice@nightware.localhost>\r\nTo: Someone New <{recipient}>\r\nSubject: {subject}\r\n"
        + $"Date: {DateTimeOffset.UtcNow:R}\r\nMessage-ID: <{Guid.NewGuid():N}@nightware.localhost>\r\nMIME-Version: 1.0\r\n"
        + "Content-Type: text/plain; charset=utf-8\r\n\r\nHello.\r\n";
}

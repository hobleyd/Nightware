using System.Text;
using Nightware.Application.Common.Query;
using Nightware.Application.Mail;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Calendar.Messages;

/// <summary>
/// Graph <c>GET messages/{id}/…/event</c>: the calendar event an invitation or response mail is about.
/// Reads the message's <c>text/calendar</c> part through the mail read port, takes its UID, and finds
/// the mailbox's own copy of the meeting (for an invitation to one occurrence, that occurrence).
/// </summary>
/// <remarks>
/// Everything that cannot be resolved is <see cref="ErrorKind.NotFound"/>, never a 5xx: NightMail tries
/// the next cast spelling, then an iCalUId lookup, then a calendarView search on 400/404. An invitation
/// (<c>METHOD:REQUEST</c>) from an organizer outside the calendar server, which the server never delivers, is
/// imported into the default calendar first, as Exchange puts an arriving invitation in the calendar; the import is
/// idempotent by UID, so this GET writes at most once per meeting.
/// </remarks>
public sealed class GetMessageEvent(IMailMessageReader mail, IICalendarCodec codec, ICalendarReader calendar, IInvitationImporter importer)
{
    /// <summary>Larger calendar parts are not meeting messages anyone sends; refusing them bounds memory.</summary>
    internal const int MaxCalendarPartBytes = 1024 * 1024;

    private const int UidMatchLimit = 10;

    public async Task<Result<CalendarEvent>> ExecuteAsync(MailboxRef mailbox, MessageId messageId, CancellationToken cancellationToken)
    {
        var invitation = await ReadInvitationAsync(mailbox, messageId, cancellationToken).ConfigureAwait(false);
        if (!invitation.IsSuccess)
        {
            return invitation.Error;
        }

        var (meeting, text, method) = invitation.Value;
        var uid = meeting.ICalUId!;
        var copies = await CopiesAsync(mailbox, uid, cancellationToken).ConfigureAwait(false);
        if (!copies.IsSuccess)
        {
            return copies.Error;
        }

        var series = copies.Value.FirstOrDefault(e => string.Equals(e.ICalUId, uid, StringComparison.Ordinal));
        if (series is null && string.Equals(method, "REQUEST", StringComparison.OrdinalIgnoreCase))
        {
            var imported = await importer.ImportAsync(mailbox, text, cancellationToken).ConfigureAwait(false);
            if (!imported.IsSuccess)
            {
                return imported.Error.Kind == ErrorKind.NotFound ? NotInCalendar() : imported.Error;
            }

            copies = await CopiesAsync(mailbox, uid, cancellationToken).ConfigureAwait(false);
            if (!copies.IsSuccess)
            {
                return copies.Error;
            }

            series = copies.Value.FirstOrDefault(e => string.Equals(e.ICalUId, uid, StringComparison.Ordinal));
        }

        if (series is null)
        {
            return NotInCalendar();
        }

        if (meeting.OriginalStart is not { } occurrence || series.Type == EventType.SingleInstance)
        {
            return series;
        }

        // Invited to one occurrence only: the copy may be just that exception, without a master.
        var exception = copies.Value.FirstOrDefault(e => string.Equals(e.ICalUId, uid, StringComparison.Ordinal) && e.OriginalStart == occurrence);
        return exception ?? await FindOccurrenceAsync(mailbox, meeting, uid, occurrence, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<IReadOnlyList<CalendarEvent>>> CopiesAsync(MailboxRef mailbox, string uid, CancellationToken cancellationToken)
    {
        var query = new ItemQuery
        {
            Filter = new FilterNode.Compare("iCalUId", CompareOp.Eq, new FilterValue.Text(uid)),
            Top = UidMatchLimit,
        };
        var copies = await calendar.ListAsync(mailbox, query, cancellationToken).ConfigureAwait(false);
        return copies.IsSuccess ? Result<IReadOnlyList<CalendarEvent>>.Success(copies.Value.Items) : copies.Error;
    }

    /// <summary>The message's meeting, the iCalendar text it came from and its iTIP method.</summary>
    private async Task<Result<(CalendarEvent Meeting, string Text, string? Method)>> ReadInvitationAsync(MailboxRef mailbox, MessageId messageId, CancellationToken cancellationToken)
    {
        var message = await mail.GetAsync(mailbox, messageId, MessageFetch.Default, cancellationToken).ConfigureAwait(false);
        if (!message.IsSuccess)
        {
            return message.Error;
        }

        if (message.Value.CalendarPart is not { } part)
        {
            return Error.NotFound("The message is not a meeting message.", "messageId");
        }

        var content = await mail.GetAttachmentContentAsync(mailbox, messageId, part.PartId, cancellationToken).ConfigureAwait(false);
        if (!content.IsSuccess)
        {
            return content.Error;
        }

        var text = await ReadTextAsync(content.Value, cancellationToken).ConfigureAwait(false);
        if (text is null)
        {
            return Unreadable();
        }

        var document = codec.Parse(text);
        var meeting = document.IsSuccess
            ? document.Value.Events.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.ICalUId))
            : null;
        return meeting is null ? Unreadable() : (meeting, text, document.Value.Method);
    }

    /// <summary>
    /// An invitation for one occurrence carries its RECURRENCE-ID. Ask the calendar view around both the
    /// original and the (possibly moved) time for the instance with that RECURRENCE-ID, so the answer is
    /// the occurrence id that an RSVP must target, not the whole series.
    /// </summary>
    private async Task<Result<CalendarEvent>> FindOccurrenceAsync(MailboxRef mailbox, CalendarEvent meeting, string uid, DateTimeOffset occurrence, CancellationToken cancellationToken)
    {
        var start = CalendarRules.ToInstant(meeting.Start);
        var end = CalendarRules.ToInstant(meeting.End);
        var from = (start < occurrence ? start : occurrence).AddMinutes(-1);
        var to = (end > occurrence ? end : occurrence).AddMinutes(1);
        var view = await calendar.CalendarViewAsync(mailbox, from, to, new ItemQuery { Top = 100 }, cancellationToken).ConfigureAwait(false);
        if (!view.IsSuccess)
        {
            return view.Error;
        }

        var instance = view.Value.Items.FirstOrDefault(e => string.Equals(e.ICalUId, uid, StringComparison.Ordinal) && e.OriginalStart == occurrence);
        return instance is null ? NotInCalendar() : instance;
    }

    private static async Task<string?> ReadTextAsync(BlobContent content, CancellationToken cancellationToken)
    {
        if (content.Length > MaxCalendarPartBytes)
        {
            return null;
        }

        var stream = await content.Open(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > MaxCalendarPartBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            // RFC 5545 §6: iCalendar's default charset is UTF-8.
            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
    }

    private static Error Unreadable() =>
        Error.NotFound("The message's meeting details could not be read.", "messageId");

    private static Error NotInCalendar() =>
        Error.NotFound("The meeting is not in the calendar.", "messageId");
}

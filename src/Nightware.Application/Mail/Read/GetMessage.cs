using System.Text;
using Nightware.Application.Calendar;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Mail.Read;

/// <summary>
/// <c>GET messages/{id}</c>: the reading-pane fetch and the plain-text body probe. For invitation
/// mail fetched without <c>$select</c> (the only form that carries <c>@odata.type</c>), the
/// <c>text/calendar</c> part is read and parsed so the meeting's time, place and type reach the
/// client (ADR 0001 §33).
/// </summary>
public sealed class GetMessage(IMailMessageReader messages, IICalendarCodec calendar)
{
    /// <summary>Invitations are small; anything larger is not an iTIP message worth parsing inline.</summary>
    internal const int MaxCalendarPartBytes = 1024 * 1024;

    private const string Attachments = "attachments";

    public async Task<Result<MessageView>> ExecuteAsync(MailboxRef mailbox, MessageId id, MessageFetch fetch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fetch);
        if (fetch.ExpandAttachments is { } expand && !string.Equals(expand.NavigationProperty, Attachments, StringComparison.OrdinalIgnoreCase))
        {
            return Error.Validation($"$expand={expand.NavigationProperty} is not supported; only attachments can be expanded.", "$expand");
        }

        var read = await messages.GetAsync(mailbox, id, fetch, cancellationToken).ConfigureAwait(false);
        if (!read.IsSuccess)
        {
            return read.Error;
        }

        var message = read.Value;
        if (message.CalendarPart is not { } part || fetch.Select is not null)
        {
            return new MessageView(message);
        }

        // The meeting facts are an enrichment: a part that cannot be read or parsed leaves the
        // message as it is (the client then falls back to its own .ics handling) rather than
        // failing the reading pane.
        var ics = await ReadCalendarPartAsync(mailbox, id, part, cancellationToken).ConfigureAwait(false);
        if (ics is null || calendar.Parse(ics) is not { IsSuccess: true } parsed
            || MeetingDetailsReader.Read(part, parsed.Value, message.From) is not { } meeting)
        {
            return new MessageView(message);
        }

        return new MessageView(message with { CalendarPart = meeting.Part }, meeting.Details);
    }

    private async Task<string?> ReadCalendarPartAsync(MailboxRef mailbox, MessageId id, CalendarPart part, CancellationToken cancellationToken)
    {
        var content = await messages.GetAttachmentContentAsync(mailbox, id, part.PartId, cancellationToken).ConfigureAwait(false);
        if (!content.IsSuccess || content.Value.Length > MaxCalendarPartBytes)
        {
            return null;
        }

        try
        {
            var stream = await content.Value.Open(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var buffer = new MemoryStream();
                await CopyBoundedAsync(stream, buffer, cancellationToken).ConfigureAwait(false);
                return buffer.Length > MaxCalendarPartBytes ? null : Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            }
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task CopyBoundedAsync(Stream source, MemoryStream target, CancellationToken cancellationToken)
    {
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            target.Write(chunk, 0, read);
            if (target.Length > MaxCalendarPartBytes)
            {
                return;
            }
        }
    }
}

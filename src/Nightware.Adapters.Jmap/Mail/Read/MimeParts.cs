using System.Text.Json;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Mail.Read;

/// <summary>Reads a JMAP <c>bodyStructure</c>: part lookup, invitation detection, attachment shapes.</summary>
internal static class MimeParts
{
    public const string Calendar = "text/calendar";
    public const string AttachedMessage = "message/rfc822";

    /// <summary>Every part, depth-first, the multipart containers included.</summary>
    public static IEnumerable<EmailBodyPart> Walk(EmailBodyPart? root)
    {
        if (root is null)
        {
            yield break;
        }

        var stack = new Stack<EmailBodyPart>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var part = stack.Pop();
            yield return part;
            if (part.SubParts is { } children)
            {
                for (var i = children.Count - 1; i >= 0; i--)
                {
                    stack.Push(children[i]);
                }
            }
        }
    }

    /// <summary>The leaf part with this JMAP <c>partId</c>, or null.</summary>
    public static EmailBodyPart? Find(EmailBodyPart? root, string partId) =>
        Walk(root).FirstOrDefault(p => p.PartId is not null && string.Equals(p.PartId, partId, StringComparison.Ordinal));

    /// <summary>
    /// The iTIP part of an invitation-related message: the first <c>text/calendar</c> part whose
    /// Content-Type carries a <c>method</c> (RFC 6047 §2.4). A calendar file without one is a
    /// published event, not a scheduling message, and stays an ordinary attachment.
    /// </summary>
    public static CalendarPart? CalendarPartOf(EmailBodyPart? root)
    {
        foreach (var part in Walk(root))
        {
            if (part.PartId is not null && IsType(part, Calendar)
                && Parameter(ContentType(part), "method") is { Length: > 0 } method)
            {
                return new CalendarPart(method.ToUpperInvariant(), new AttachmentId(part.PartId));
            }
        }

        return null;
    }

    public static bool IsType(EmailBodyPart part, string type) =>
        string.Equals(part.Type, type, StringComparison.OrdinalIgnoreCase);

    /// <summary>The part's raw Content-Type header (fetched as <c>header:Content-Type</c>), or null.</summary>
    public static string? ContentType(EmailBodyPart part)
    {
        ArgumentNullException.ThrowIfNull(part);
        if (part.Extra is null)
        {
            return null;
        }

        foreach (var (key, value) in part.Extra)
        {
            if (string.Equals(key, EmailProperties.PartContentTypeProperty, StringComparison.OrdinalIgnoreCase) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    /// <summary>A parameter of a MIME header value, e.g. <c>method</c> of <c>text/calendar; method=REQUEST</c>.</summary>
    public static string? Parameter(string? headerValue, string name)
    {
        if (string.IsNullOrEmpty(headerValue))
        {
            return null;
        }

        foreach (var segment in headerValue.Split(';').Skip(1))
        {
            var eq = segment.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0 || !string.Equals(segment[..eq].Trim(), name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = segment[(eq + 1)..].Trim();
            return value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
        }

        return null;
    }

    /// <summary>
    /// The message's attachments: JMAP's <c>attachments</c> list plus the iTIP part, which RFC 8621
    /// leaves out when it sits in a <c>multipart/alternative</c> (the client looks for it by type).
    /// </summary>
    public static IReadOnlyList<MailAttachment> Attachments(Email email)
    {
        ArgumentNullException.ThrowIfNull(email);
        var parts = (email.Attachments ?? []).Where(static p => p.PartId is not null).ToList();
        if (CalendarPartOf(email.BodyStructure) is { } calendar
            && !parts.Any(p => p.PartId == calendar.PartId.Value)
            && Find(email.BodyStructure, calendar.PartId.Value) is { } calendarPart)
        {
            parts.Add(calendarPart);
        }

        return [.. parts.Select(p => ToAttachment(p, email.ReceivedAt))];
    }

    public static MailAttachment ToAttachment(EmailBodyPart part, DateTimeOffset? lastModified)
    {
        ArgumentNullException.ThrowIfNull(part);
        var isItem = IsType(part, AttachedMessage);
        var type = string.IsNullOrWhiteSpace(part.Type) ? "application/octet-stream" : part.Type.ToLowerInvariant();
        var name = !string.IsNullOrWhiteSpace(part.Name) ? part.Name
            : isItem ? "Attached message"
            : IsType(part, Calendar) ? "invite.ics"
            : "attachment";
        var cid = string.IsNullOrWhiteSpace(part.Cid) ? null : part.Cid.Trim().Trim('<', '>');
        // Inline means "referenced from the body by cid:", as Exchange reports it. A Content-ID is
        // required: Sabre marks its invitation .ics `inline` without one, and the client must treat
        // that as a file, not an image to fetch for the body.
        var isInline = cid is not null && !string.Equals(part.Disposition, "attachment", StringComparison.OrdinalIgnoreCase);
        return new MailAttachment(new AttachmentId(part.PartId!), name, type, part.Size ?? 0, isItem ? AttachmentKind.Item : AttachmentKind.File)
        {
            IsInline = isInline,
            ContentId = cid,
            LastModifiedDateTime = lastModified,
        };
    }
}

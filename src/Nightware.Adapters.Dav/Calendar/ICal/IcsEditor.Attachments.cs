using System.Globalization;
using Ical.Net.DataTypes;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Calendar.ICal;

/// <summary>An event's URI attachment, with the RFC 8607 <c>FILENAME</c>/<c>SIZE</c> parameters.</summary>
public sealed record EventAttachmentLink(Uri Uri, string FileName, string ContentType, long? Size);

/// <summary>URI attachments on the series master (or single event), as To Do attachments are kept.</summary>
public sealed partial class IcsEditor
{
    private const string FileNameParameter = "FILENAME";
    private const string SizeParameter = "SIZE";

    public static Result<IReadOnlyList<EventAttachmentLink>> ReadAttachments(string ics)
    {
        var loaded = IcalNetCalendarCodec.Load(ics);
        if (!loaded.IsSuccess)
        {
            return loaded.Error;
        }

        var master = loaded.Value.Events.FirstOrDefault(static e => e.RecurrenceIdentifier is null) ?? loaded.Value.Events.FirstOrDefault();
        IReadOnlyList<EventAttachmentLink> links = master is null
            ? []
            : [.. master.Attachments.Where(static a => a.Uri is { IsAbsoluteUri: true }).Select(static a => new EventAttachmentLink(
                a.Uri!,
                a.Parameters.Get(FileNameParameter) is { Length: > 0 } name ? name : Uri.UnescapeDataString(a.Uri!.AbsolutePath.Split('/')[^1]),
                string.IsNullOrWhiteSpace(a.FormatType) ? "application/octet-stream" : a.FormatType,
                long.TryParse(a.Parameters.Get(SizeParameter), NumberStyles.None, CultureInfo.InvariantCulture, out var size) ? size : null))];
        return Result<IReadOnlyList<EventAttachmentLink>>.Success(links);
    }

    public Result<string> AddAttachment(string ics, EventAttachmentLink link) =>
        Edit(ics, calendar =>
        {
            ArgumentNullException.ThrowIfNull(link);
            var master = calendar.Events.FirstOrDefault(static e => e.RecurrenceIdentifier is null) ?? calendar.Events.FirstOrDefault();
            if (master is null)
            {
                return Error.NotFound("The resource holds no event.");
            }

            var attachment = new Attachment { Uri = link.Uri, FormatType = link.ContentType };
            attachment.Parameters.Set(FileNameParameter, link.FileName);
            if (link.Size is { } size)
            {
                attachment.Parameters.Set(SizeParameter, size.ToString(CultureInfo.InvariantCulture));
            }

            master.Attachments.Add(attachment);
            Touch(master);
            return Result.Success();
        });

    public Result<string> RemoveAttachment(string ics, Uri uri) =>
        Edit(ics, calendar =>
        {
            var removed = false;
            foreach (var vevent in calendar.Events)
            {
                foreach (var attachment in vevent.Attachments.Where(a => a.Uri is { } u && u.AbsolutePath == uri.AbsolutePath).ToList())
                {
                    vevent.Attachments.Remove(attachment);
                    removed = true;
                }

                if (removed)
                {
                    Touch(vevent);
                }
            }

            return removed ? Result.Success() : Error.NotFound("The attachment does not exist.");
        });
}

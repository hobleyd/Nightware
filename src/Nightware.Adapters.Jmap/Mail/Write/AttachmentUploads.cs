using System.Net.Http.Headers;
using Nightware.Adapters.Jmap.Core.Blobs;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Mail.Write;

/// <summary>Uploads new attachments as blobs and turns them into create parts.</summary>
internal static class AttachmentUploads
{
    public const string FallbackType = "application/octet-stream";

    public static async Task<Result<IReadOnlyList<EmailBodyPart>>> UploadAsync(
        IJmapBlobService blobs, string accountId, IReadOnlyList<NewAttachment> attachments, CancellationToken cancellationToken)
    {
        var parts = new List<EmailBodyPart>(attachments.Count);
        foreach (var attachment in attachments)
        {
            var uploaded = await UploadAsync(blobs, accountId, attachment, cancellationToken).ConfigureAwait(false);
            if (!uploaded.IsSuccess)
            {
                return uploaded.Error;
            }

            parts.Add(uploaded.Value);
        }

        return parts;
    }

    public static async Task<Result<EmailBodyPart>> UploadAsync(IJmapBlobService blobs, string accountId, NewAttachment attachment, CancellationToken cancellationToken)
    {
        var type = MediaType(attachment.ContentType);
        using var content = new MemoryStream(attachment.Content.ToArray(), writable: false);
        var uploaded = await blobs.UploadAsync(accountId, content, type, cancellationToken).ConfigureAwait(false);
        return uploaded.Map(blob => new EmailBodyPart
        {
            BlobId = blob.BlobId,
            Type = type,
            Name = attachment.Name,
            Disposition = attachment.IsInline ? "inline" : "attachment",
            Cid = string.IsNullOrWhiteSpace(attachment.ContentId) ? null : attachment.ContentId.Trim().Trim('<', '>'),
            Size = blob.Size,
        });
    }

    /// <summary>The client's type when it is a valid media type; anything else is sent as opaque bytes.</summary>
    public static string MediaType(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var parsed) && parsed.MediaType is { } media && media.Contains('/', StringComparison.Ordinal)
            ? media.ToLowerInvariant()
            : FallbackType;
}

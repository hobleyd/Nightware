using Nightware.Adapters.Jmap.Core;
using Nightware.Adapters.Jmap.Core.Blobs;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Core.Requests;
using Nightware.Adapters.Jmap.Core.Session;
using Nightware.Adapters.Jmap.Mail.Ids;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Mail.Write;

/// <summary>The attachment parts of a <see cref="DraftContent"/>: the original's (forward) followed by new uploads.</summary>
internal static class DraftAttachments
{
    public static async Task<Result<IReadOnlyList<EmailBodyPart>>> ForContentAsync(
        IJmapClient jmap, IJmapBlobService blobs, IJmapMessageLocator locator, JmapAccount account, DraftContent content, CancellationToken cancellationToken)
    {
        IReadOnlyList<EmailBodyPart> carried = [];
        if (content.AttachmentsFrom is { } source)
        {
            var original = await OfMessageAsync(jmap, locator, account, source, cancellationToken).ConfigureAwait(false);
            if (!original.IsSuccess)
            {
                return original.Error;
            }

            carried = original.Value;
        }

        if (content.Attachments.Count == 0)
        {
            return Result<IReadOnlyList<EmailBodyPart>>.Success(carried);
        }

        var uploaded = await AttachmentUploads.UploadAsync(blobs, account.AccountId, content.Attachments, cancellationToken).ConfigureAwait(false);
        return uploaded.Map(parts => (IReadOnlyList<EmailBodyPart>)[.. carried, .. parts]);
    }

    /// <summary>Every attachment of <paramref name="id"/>, by blobId, ready to be carried into a new Email.</summary>
    public static async Task<Result<IReadOnlyList<EmailBodyPart>>> OfMessageAsync(
        IJmapClient jmap, IJmapMessageLocator locator, JmapAccount account, MessageId id, CancellationToken cancellationToken)
    {
        var jmapId = await locator.ResolveAsync(account, id, cancellationToken).ConfigureAwait(false);
        if (!jmapId.IsSuccess)
        {
            return jmapId.Error;
        }

        var request = new JmapRequestBuilder();
        var get = request.Add(JmapMethods.EmailGet, new EmailGetRequest(account.AccountId)
        {
            Ids = [jmapId.Value],
            Properties = ExistingEmail.AttachmentProperties,
            BodyProperties = ExistingEmail.BodyProperties,
        });
        var response = await jmap.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.Bind(r => r.Get(get)).Bind(WriteSupport.Single).Map(ExistingEmail.Attachments);
    }
}

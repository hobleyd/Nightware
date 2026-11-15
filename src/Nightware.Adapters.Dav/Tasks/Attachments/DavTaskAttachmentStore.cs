using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Tasks.ICal;
using Nightware.Application.Tasks;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;
using Nightware.Domain.Tasks;

namespace Nightware.Adapters.Dav.Tasks.Attachments;

/// <summary>
/// <see cref="ITaskAttachmentStore"/>: the bytes go to Nextcloud Files (<see cref="TaskAttachmentFiles"/>),
/// the task gets an <c>ATTACH</c> pointing at them. An attachment id is the file's
/// <see cref="DavItemId"/>, and only ids the task's own <c>ATTACH</c> list names are served.
/// </summary>
/// <remarks>
/// An upload session is stateless (<see cref="TaskUploadToken"/>) and completed by one PUT of the
/// whole file, which is streamed to Nextcloud as it arrives.
/// </remarks>
internal sealed class DavTaskAttachmentStore(
    DavTaskCollections collections,
    DavTaskStore tasks,
    IDavClient dav,
    VTodoCodec codec,
    TaskAttachmentFiles files,
    TimeProvider time) : ITaskAttachmentStore
{
    /// <summary>How long an upload session stays open (Graph's are similarly short-lived).</summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(15);

    public async Task<Result<IReadOnlyList<TaskAttachment>>> ListAsync(MailboxRef mailbox, TaskListId list, TaskId task, CancellationToken cancellationToken)
    {
        var links = await LinksAsync(mailbox, list, task, cancellationToken).ConfigureAwait(false);
        return links.Map(IReadOnlyList<TaskAttachment> (l) => [.. l.Select(x => x.Attachment)]);
    }

    public async Task<Result<BlobContent>> GetContentAsync(MailboxRef mailbox, TaskListId list, TaskId task, AttachmentId attachment, CancellationToken cancellationToken)
    {
        var links = await LinksAsync(mailbox, list, task, cancellationToken).ConfigureAwait(false);
        if (!links.IsSuccess)
        {
            return links.Error;
        }

        var match = links.Value.FirstOrDefault(l => string.Equals(l.Attachment.Id.Value, attachment.Value, StringComparison.Ordinal));
        if (match is null)
        {
            return Error.NotFound("The specified object was not found in the store.", "id");
        }

        var download = await dav.OpenReadAsync(match.Href, cancellationToken).ConfigureAwait(false);
        if (!download.IsSuccess)
        {
            return download.Error;
        }

        Stream body = new DownloadStream(download.Value);
        return new BlobContent(match.Attachment.ContentType, download.Value.Length ?? match.Attachment.Size, _ => Task.FromResult(body))
        {
            FileName = match.Attachment.Name,
        };
    }

    public async Task<Result<TaskAttachment>> CreateAsync(MailboxRef mailbox, TaskListId list, TaskId task, NewAttachment attachment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        var href = await tasks.ResolveAsync(mailbox, list, task, cancellationToken).ConfigureAwait(false);
        if (!href.IsSuccess)
        {
            return href.Error;
        }

        using var content = new MemoryStream(attachment.Content.ToArray(), writable: false);
        return await AttachAsync(mailbox, href.Value, attachment.Name, attachment.ContentType, content, attachment.Content.Length, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>NightMail never removes a task attachment, so neither does the façade.</summary>
    public Task<Result> DeleteAsync(MailboxRef mailbox, TaskListId list, TaskId task, AttachmentId attachment, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Failure(Error.Unsupported("Removing task attachments is not supported.")));

    public async Task<Result<UploadSession>> BeginUploadAsync(
        MailboxRef mailbox, TaskListId list, TaskId task, string name, string contentType, long size, CancellationToken cancellationToken)
    {
        var listHref = await collections.ResolveListAsync(mailbox, list, cancellationToken).ConfigureAwait(false);
        var taskHref = listHref.Bind(l => DavTaskCollections.ResolveTask(l, task));
        if (!taskHref.IsSuccess)
        {
            return taskHref.Error;
        }

        // Fail now, not after the client has sent megabytes, when the task does not exist.
        var exists = await dav.GetAsync(taskHref.Value, cancellationToken).ConfigureAwait(false);
        if (!exists.IsSuccess)
        {
            return exists.Error;
        }

        var expires = time.GetUtcNow() + SessionLifetime;
        var token = new TaskUploadToken(mailbox.Address, listHref.Value, taskHref.Value, name, contentType, size, expires.ToUnixTimeSeconds());
        return new UploadSession(token.Encode(), DateTimeOffset.FromUnixTimeSeconds(token.ExpiresAtUnixSeconds), size);
    }

    public async Task<Result<UploadProgress>> PutChunkAsync(UploadSession session, long rangeStart, long rangeEnd, Stream chunk, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(chunk);
        var decoded = TaskUploadToken.Decode(session.Token, time.GetUtcNow());
        if (!decoded.IsSuccess)
        {
            return decoded.Error;
        }

        var token = decoded.Value;
        if (rangeStart != 0 || (rangeEnd != token.Size - 1 && rangeEnd != UploadTaskAttachmentContent.ToEnd))
        {
            return Error.Validation($"Send the whole file (bytes 0-{token.Size - 1}) in one request.", "Content-Range");
        }

        // Re-check the stored target exactly as a fresh request would.
        var mailbox = new MailboxRef(token.Mailbox);
        var href = await tasks.ResolveAsync(mailbox, DavItemId.Resource(token.ListHref).ToTaskListId(), DavItemId.Resource(token.TaskHref).ToTaskId(), cancellationToken).ConfigureAwait(false);
        if (!href.IsSuccess)
        {
            return href.Error;
        }

        var attached = await AttachAsync(mailbox, href.Value, token.Name, token.ContentType, chunk, token.Size, cancellationToken).ConfigureAwait(false);
        return attached.Map(a => new UploadProgress([], a));
    }

    private async Task<Result<TaskAttachment>> AttachAsync(
        MailboxRef mailbox, string taskHref, string name, string contentType, Stream content, long size, CancellationToken cancellationToken)
    {
        var principal = await collections.PrincipalAsync(mailbox, cancellationToken).ConfigureAwait(false);
        var home = principal.Bind(files.HomeOf);
        if (!home.IsSuccess)
        {
            return home.Error;
        }

        // Check the task first so a missing one does not leave an orphaned file behind.
        var exists = await dav.GetAsync(taskHref, cancellationToken).ConfigureAwait(false);
        if (!exists.IsSuccess)
        {
            return exists.Error;
        }

        var stored = await files.StoreAsync(home.Value, name, contentType, content, size, cancellationToken).ConfigureAwait(false);
        if (!stored.IsSuccess)
        {
            return stored.Error;
        }

        var (fileHref, length) = stored.Value;
        var link = new TodoAttachmentLink(files.ToUri(fileHref), name, contentType) { Size = length };
        var edited = await tasks.EditAsync(taskHref, ics => codec.AddAttachment(ics, link), cancellationToken).ConfigureAwait(false);
        if (!edited.IsSuccess)
        {
            await files.DeleteFolderOfAsync(fileHref, cancellationToken).ConfigureAwait(false);
            return edited.Error;
        }

        return new TaskAttachment(DavItemId.Resource(fileHref).ToAttachmentId(), name, contentType, length)
        {
            LastModifiedDateTime = time.GetUtcNow(),
        };
    }

    private async Task<Result<IReadOnlyList<StoredAttachment>>> LinksAsync(MailboxRef mailbox, TaskListId list, TaskId task, CancellationToken cancellationToken)
    {
        var href = await tasks.ResolveAsync(mailbox, list, task, cancellationToken).ConfigureAwait(false);
        if (!href.IsSuccess)
        {
            return href.Error;
        }

        var resource = await dav.GetAsync(href.Value, cancellationToken).ConfigureAwait(false);
        var links = resource.IsSuccess ? codec.ReadAttachments(resource.Value.Body) : resource.Error;
        if (!links.IsSuccess)
        {
            return links.Error;
        }

        IReadOnlyList<StoredAttachment> stored = links.Value
            .Select(l => (Link: l, Href: files.ToHref(l.Uri)))
            .Where(l => l.Href is not null)
            .Select(l => new StoredAttachment(
                l.Href!,
                new TaskAttachment(DavItemId.Resource(l.Href!).ToAttachmentId(), l.Link.FileName, l.Link.ContentType, l.Link.Size ?? 0)))
            .ToList();
        return Result<IReadOnlyList<StoredAttachment>>.Success(stored);
    }

    private sealed record StoredAttachment(string Href, TaskAttachment Attachment);
}

internal static class DavAttachmentIds
{
    public static AttachmentId ToAttachmentId(this DavItemId id) => new(id.ToString());
}

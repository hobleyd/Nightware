using Nightware.Adapters.Dav.Calendar.ICal;
using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Tasks.Attachments;
using Nightware.Application.Calendar.Calendars;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Calendar;

/// <summary>
/// Event attachments (ADR 0006 D6): the bytes go to the owner's Nextcloud Files under
/// <c>Nightware/Event attachments/{uuid}/{name}</c> and the event gets an <c>ATTACH</c> URL, as To Do attachments
/// do. An attachment id is the file's DAV id, and only files the event's own <c>ATTACH</c> list names are served.
/// </summary>
internal sealed class DavEventAttachmentStore(DavEventResources resources, DavCalendarScopes scopes, TaskAttachmentFiles files, IcsEditor editor, IDavClient dav)
    : IEventAttachmentStore
{
    private static readonly string[] Folder = ["Nightware", "Event attachments"];

    public async Task<Result<IReadOnlyList<EventAttachment>>> ListAsync(MailboxRef mailbox, EventId eventId, CancellationToken cancellationToken)
    {
        var stored = await resources.LoadAsync(mailbox, eventId, cancellationToken).ConfigureAwait(false);
        return stored.IsSuccess ? Linked(stored.Value).Map(static l => (IReadOnlyList<EventAttachment>)[.. l.Select(static x => x.Attachment)]) : stored.Error;
    }

    public async Task<Result<EventAttachment>> GetAsync(MailboxRef mailbox, EventId eventId, AttachmentId id, CancellationToken cancellationToken)
    {
        var found = await FindAsync(mailbox, eventId, id, cancellationToken).ConfigureAwait(false);
        if (!found.IsSuccess)
        {
            return found.Error;
        }

        var download = await dav.OpenReadAsync(found.Value.Href, cancellationToken).ConfigureAwait(false);
        if (!download.IsSuccess)
        {
            return download.Error;
        }

        using var file = download.Value;
        using var buffer = new MemoryStream();
        await file.Content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        return found.Value.Attachment with { Content = buffer.ToArray(), Size = buffer.Length };
    }

    public async Task<Result<EventAttachment>> AddAsync(MailboxRef mailbox, EventId eventId, string name, string contentType, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        var stored = await resources.LoadAsync(mailbox, eventId, cancellationToken).ConfigureAwait(false);
        if (!stored.IsSuccess)
        {
            return stored.Error;
        }

        var scope = await scopes.ResolveAsync(mailbox, cancellationToken).ConfigureAwait(false);
        var home = scope.Bind(s => files.HomeOf(s.Owner));
        if (!home.IsSuccess)
        {
            return home.Error;
        }

        using var body = new MemoryStream(content.ToArray());
        var file = await files.StoreAsync(home.Value, Folder, name, contentType, body, content.Length, cancellationToken).ConfigureAwait(false);
        if (!file.IsSuccess)
        {
            return file.Error;
        }

        var link = new EventAttachmentLink(files.ToUri(file.Value.Href), TaskAttachmentFiles.SafeName(name), contentType, file.Value.Size);
        var edited = editor.AddAttachment(stored.Value.Body, link);
        var saved = edited.IsSuccess ? await resources.SaveAsync(stored.Value, edited.Value, cancellationToken).ConfigureAwait(false) : edited.ToResult();
        if (!saved.IsSuccess)
        {
            await files.DeleteFolderOfAsync(file.Value.Href, cancellationToken).ConfigureAwait(false);
            return saved.Error;
        }

        return new EventAttachment(DavItemId.Resource(file.Value.Href).ToAttachmentId(), link.FileName, contentType, file.Value.Size);
    }

    public async Task<Result> DeleteAsync(MailboxRef mailbox, EventId eventId, AttachmentId id, CancellationToken cancellationToken)
    {
        var found = await FindAsync(mailbox, eventId, id, cancellationToken).ConfigureAwait(false);
        if (!found.IsSuccess)
        {
            return found.Error;
        }

        var edited = editor.RemoveAttachment(found.Value.Stored.Body, found.Value.Uri);
        var saved = edited.IsSuccess ? await resources.SaveAsync(found.Value.Stored, edited.Value, cancellationToken).ConfigureAwait(false) : edited.ToResult();
        if (saved.IsSuccess)
        {
            await files.DeleteFolderOfAsync(found.Value.Href, cancellationToken).ConfigureAwait(false);
        }

        return saved;
    }

    private sealed record LinkedFile(EventAttachment Attachment, string Href, Uri Uri, StoredEvent Stored);

    private Result<List<LinkedFile>> Linked(StoredEvent stored) =>
        IcsEditor.ReadAttachments(stored.Body).Map(links => links
            .Select(l => (Link: l, Href: files.ToHref(l.Uri)))
            .Where(static x => x.Href is not null)
            .Select(x => new LinkedFile(new EventAttachment(DavItemId.Resource(x.Href!).ToAttachmentId(), x.Link.FileName, x.Link.ContentType, x.Link.Size ?? 0), x.Href!, x.Link.Uri, stored))
            .ToList());

    private async Task<Result<LinkedFile>> FindAsync(MailboxRef mailbox, EventId eventId, AttachmentId id, CancellationToken cancellationToken)
    {
        var stored = await resources.LoadAsync(mailbox, eventId, cancellationToken).ConfigureAwait(false);
        if (!stored.IsSuccess)
        {
            return stored.Error;
        }

        var linked = Linked(stored.Value);
        return linked.Bind(l => l.FirstOrDefault(x => x.Attachment.Id == id) is { } found
            ? Result<LinkedFile>.Success(found)
            : Error.NotFound("The attachment does not exist."));
    }
}

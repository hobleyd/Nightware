using Nightware.Application.Common.Query;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Application.Mail;

/// <summary>What to fetch for a single message beyond the list projection.</summary>
/// <param name="Select">Graph property names requested; null = Graph default projection.</param>
/// <param name="PreferredBodyType">From <c>Prefer: outlook.body-content-type</c>; null = as stored (HTML when available).</param>
/// <param name="ExpandAttachments">The <c>$expand=attachments(...)</c> clause, if any.</param>
public sealed record MessageFetch(
    IReadOnlySet<string>? Select = null,
    BodyType? PreferredBodyType = null,
    ExpandClause? ExpandAttachments = null)
{
    public static MessageFetch Default { get; } = new();

    public bool Selects(string property) => Select is null || Select.Contains(property);
}

/// <summary>Options for one delta round. <c>$select</c>/<c>$top</c> are frozen into the link by the Api.</summary>
public sealed record DeltaRequest(int PageSize, IReadOnlySet<string>? Select = null)
{
    public const int DefaultPageSize = 50;

    public static DeltaRequest Default { get; } = new(DefaultPageSize);
}

public interface IMailFolderReader
{
    /// <param name="parent">Null lists the top-level folders (children of the root).</param>
    Task<Result<Page<MailFolder>>> ListAsync(MailboxRef mailbox, FolderRef? parent, ItemQuery query, CancellationToken cancellationToken);

    Task<Result<MailFolder>> GetAsync(MailboxRef mailbox, FolderRef folder, CancellationToken cancellationToken);
}

public interface IMailMessageReader
{
    /// <param name="folder">Null searches the whole mailbox (<c>GET /messages</c>).</param>
    Task<Result<Page<MailMessage>>> ListAsync(MailboxRef mailbox, FolderRef? folder, ItemQuery query, CancellationToken cancellationToken);

    Task<Result<MailMessage>> GetAsync(MailboxRef mailbox, MessageId id, MessageFetch fetch, CancellationToken cancellationToken);

    /// <summary>The full RFC 5322 message (<c>GET messages/{id}/$value</c>).</summary>
    Task<Result<BlobContent>> GetRawAsync(MailboxRef mailbox, MessageId id, CancellationToken cancellationToken);

    Task<Result<IReadOnlyList<MailAttachment>>> ListAttachmentsAsync(MailboxRef mailbox, MessageId id, CancellationToken cancellationToken);

    /// <param name="includeContent">Populate <see cref="MailAttachment.ContentBytes"/>.</param>
    Task<Result<MailAttachment>> GetAttachmentAsync(MailboxRef mailbox, MessageId id, AttachmentId attachment, bool includeContent, CancellationToken cancellationToken);

    Task<Result<BlobContent>> GetAttachmentContentAsync(MailboxRef mailbox, MessageId id, AttachmentId attachment, CancellationToken cancellationToken);
}

/// <summary>The folder hierarchy's delta feed (Graph <c>mailFolders/delta</c>, ADR 0006 D5).</summary>
public interface IMailFolderDeltaReader
{
    /// <param name="token">Null starts an initial sync: every folder, then a token for what changes after.</param>
    Task<Result<DeltaPage<MailFolder>>> DeltaAsync(MailboxRef mailbox, DeltaToken? token, DeltaRequest request, CancellationToken cancellationToken);
}

public interface IMailDeltaReader
{
    /// <param name="token">Null starts an initial sync; otherwise a token from a previous page.</param>
    Task<Result<DeltaPage<MailMessage>>> DeltaAsync(MailboxRef mailbox, FolderRef folder, DeltaToken? token, DeltaRequest request, CancellationToken cancellationToken);
}

/// <summary>Recent correspondents of a mailbox, consumed by People ranking.</summary>
public interface IRecentCorrespondents
{
    Task<Result<IReadOnlyList<Correspondent>>> ListAsync(MailboxRef mailbox, int max, CancellationToken cancellationToken);
}

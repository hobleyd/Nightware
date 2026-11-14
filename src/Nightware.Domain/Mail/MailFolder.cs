using Nightware.Domain.Kernel;

namespace Nightware.Domain.Mail;

/// <summary>Graph well-known folder names the client addresses folders by.</summary>
public enum WellKnownFolder
{
    Inbox,
    SentItems,
    Drafts,
    DeletedItems,
    JunkEmail,
    Archive,
    Outbox,
    MsgFolderRoot,
}

/// <summary>A folder addressed either by id or by Graph well-known name.</summary>
public abstract record FolderRef
{
    private FolderRef()
    {
    }

    public sealed record ById(FolderId Id) : FolderRef;

    public sealed record WellKnown(WellKnownFolder Name) : FolderRef;
}

public sealed record MailFolder(
    FolderId Id,
    string DisplayName,
    FolderId? ParentFolderId,
    int ChildFolderCount,
    int UnreadItemCount,
    int TotalItemCount)
{
    public WellKnownFolder? WellKnownName { get; init; }

    public bool IsHidden { get; init; }
}

using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Api.Features.Mail.Read;

/// <summary>
/// Reads a <c>{folderIdOrWellKnownName}</c> route segment. Graph's well-known names are a fixed,
/// case-insensitive vocabulary (not ids, which stay opaque to the Api); anything else is an id.
/// </summary>
public static class FolderReference
{
    private static readonly Dictionary<string, WellKnownFolder> WellKnown = new(StringComparer.OrdinalIgnoreCase)
    {
        ["inbox"] = WellKnownFolder.Inbox,
        ["sentitems"] = WellKnownFolder.SentItems,
        ["drafts"] = WellKnownFolder.Drafts,
        ["deleteditems"] = WellKnownFolder.DeletedItems,
        ["junkemail"] = WellKnownFolder.JunkEmail,
        ["archive"] = WellKnownFolder.Archive,
        ["outbox"] = WellKnownFolder.Outbox,
        ["msgfolderroot"] = WellKnownFolder.MsgFolderRoot,
    };

    public static FolderRef Parse(string segment)
    {
        ArgumentNullException.ThrowIfNull(segment);
        return WellKnown.TryGetValue(segment, out var name) ? new FolderRef.WellKnown(name) : new FolderRef.ById(new FolderId(segment));
    }
}

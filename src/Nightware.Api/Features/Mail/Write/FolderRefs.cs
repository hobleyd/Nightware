using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Api.Features.Mail.Write;

/// <summary>A folder segment or <c>destinationId</c>: a Graph well-known name (case-insensitive) or an opaque folder id.</summary>
public static class FolderRefs
{
    private static readonly Dictionary<string, WellKnownFolder> WellKnownNames = new(StringComparer.OrdinalIgnoreCase)
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

    public static Result<FolderRef> Parse(string? value, string target)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Error.Validation($"{target} is required.", target);
        }

        var trimmed = value.Trim();
        return WellKnownNames.TryGetValue(trimmed, out var name)
            ? new FolderRef.WellKnown(name)
            : new FolderRef.ById(new FolderId(trimmed));
    }
}

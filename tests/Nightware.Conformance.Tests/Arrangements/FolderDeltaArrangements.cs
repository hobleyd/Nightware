using Nightware.Application.Mail;
using Nightware.Conformance.Tests.Harness;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Conformance.Tests.Arrangements;

/// <summary>Phase 12 <c>mailFolders/delta</c> (ADR 0006 D5), against captures from Microsoft 365.</summary>
public sealed class FolderDeltaArrangements : IConformanceArrangements
{
    private static readonly MailboxRef Me = new("me@example.com");
    private const string Root = "AAMk-parentFolderId-0002=";

    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        // Graph paged its initial sync; the façade sends its (small) tree in one page, so the arrangement
        // pages here to compare the first page as captured.
        yield return new ConformanceArrangement("mail.folders.delta", static fakes =>
            fakes.For<IMailFolderDeltaReader>()
                .DeltaAsync(Me, null, Arg.Any<DeltaRequest>(), Arg.Any<CancellationToken>())
                .Returns(DeltaPage<MailFolder>.More(
                    [
                        Folder("AAMk-id-0012=", "Archive", Root), Folder("AAMk-id-0013=", "Conversation History", Root),
                        Folder("AAMk-id-0014=", "Deleted Items", Root), Folder("AAMk-id-0015=", "Drafts", Root),
                        Folder("AAMk-id-0001=", "Inbox", Root), Folder("AAMk-id-0016=", "Projects", "AAMk-id-0001="),
                        Folder("AAMk-id-0017=", "Receipts", "AAMk-id-0001="), Folder("AAMk-id-0018=", "Junk Email", Root),
                        Folder("AAMk-parentFolderId-0001=", "nw-capture-eb09af", Root), Folder("AAMk-id-0019=", "Outbox", Root),
                    ],
                    new DeltaToken("page-2"))))
        {
            IgnorePaths = DeltaTypes,
        };

        yield return new ConformanceArrangement("mail.folders.delta-changes", static fakes =>
            fakes.For<IMailFolderDeltaReader>()
                .DeltaAsync(Me, new DeltaToken("since-rename"), Arg.Any<DeltaRequest>(), Arg.Any<CancellationToken>())
                .Returns(DeltaPage<MailFolder>.Done([Folder("AAMk-parentFolderId-0001=", "nw-capture-eb09af-renamed", Root)], new DeltaToken("after"))))
        {
            RequestToken = new DeltaToken("since-rename"),
            RequestTokenSelect = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "displayName", "parentFolderId" },
            IgnorePaths = DeltaTypes,
        };
    }

    /// <summary>
    /// The façade drops <c>@odata.type</c> from projected items (ADR 0001 §14); Graph's folder delta keeps it.
    /// A feed of one entity set carries nothing in it a client needs.
    /// </summary>
    private static readonly IReadOnlyList<string> DeltaTypes = ["value/*/@odata.type"];

    private static DeltaEntry<MailFolder> Folder(string id, string name, string parent) =>
        new DeltaEntry<MailFolder>.Upsert(new MailFolder(new FolderId(id), name, new FolderId(parent), 0, 0, 0));
}

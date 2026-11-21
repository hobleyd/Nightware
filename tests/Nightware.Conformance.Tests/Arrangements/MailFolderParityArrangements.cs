using Nightware.Application.Mail;
using Nightware.Conformance.Tests.Harness;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Conformance.Tests.Arrangements;

/// <summary>Phase 19: a top-level folder create and a folder copy.</summary>
public sealed class MailFolderParityArrangements : IConformanceArrangements
{
    private static readonly MailboxRef Me = new("me@example.com");

    private const string RootId = "AAMkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OAAuAAAAAAAiQ8W967B7TKBjgx9rVEURAQAiIsqMbYjsT5e-T7KzowPTAAAAAAEIAAA=";
    private const string InboxId = "AAMkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OAAuAAAAAAAiQ8W967B7TKBjgx9rVEURAQAiIsqMbYjsT5e-T7KzowPTAAAAAAEMAAA=";
    private const string ProjectsId = "AAMkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OAAuAAAAAAAiQ8W967B7TKBjgx9rVEURAQAiIsqMbYjsT5e-T7KzowPTAAAYbvZFAAA=";
    private const string CopyId = "AAMkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OAAuAAAAAAAiQ8W967B7TKBjgx9rVEURAQAiIsqMbYjsT5e-T7KzowPTAAAYbvZGAAA=";
    private const string ClutterId = "AAMkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OAAuAAAAAAAiQ8W967B7TKBjgx9rVEURAQAiIsqMbYjsT5e-T7KzowPTAAAYbvZHAAA=";

    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        yield return new ConformanceArrangement("mail.folders.create", static fakes =>
            fakes.For<IMailFolderWriter>()
                .CreateAsync(Me, new FolderRef.WellKnown(WellKnownFolder.MsgFolderRoot), "Clutter", Arg.Any<CancellationToken>())
                .Returns(new MailFolder(new FolderId(ClutterId), "Clutter", new FolderId(RootId), 0, 0, 0)));

        yield return new ConformanceArrangement("mail.folders.copy", static fakes =>
            fakes.For<IMailFolderWriter>()
                .CopyAsync(Me, new FolderId(ProjectsId), new FolderRef.WellKnown(WellKnownFolder.Inbox), Arg.Any<CancellationToken>())
                .Returns(new MailFolder(new FolderId(CopyId), "Projects", new FolderId(InboxId), 0, 1, 12)));
    }
}

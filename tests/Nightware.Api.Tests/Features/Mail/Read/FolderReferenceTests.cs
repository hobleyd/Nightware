using Nightware.Api.Features.Mail.Read;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Api.Tests.Features.Mail.Read;

public sealed class FolderReferenceTests
{
    [Theory]
    [InlineData("inbox", WellKnownFolder.Inbox)]
    [InlineData("SentItems", WellKnownFolder.SentItems)]
    [InlineData("drafts", WellKnownFolder.Drafts)]
    [InlineData("deleteditems", WellKnownFolder.DeletedItems)]
    [InlineData("junkemail", WellKnownFolder.JunkEmail)]
    [InlineData("archive", WellKnownFolder.Archive)]
    [InlineData("outbox", WellKnownFolder.Outbox)]
    [InlineData("msgfolderroot", WellKnownFolder.MsgFolderRoot)]
    public void Well_known_names_are_case_insensitive(string segment, WellKnownFolder expected)
    {
        FolderReference.Parse(segment).ShouldBe(new FolderRef.WellKnown(expected));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("AAMkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OAAuAAAAAAAiQ8W967B7TKBjgx9rVEURAQAiIsqMbYjsT5e-T7KzowPTAAAAAAEMAAA=")]
    [InlineData("inboxes")]
    public void Anything_else_is_an_opaque_id(string segment)
    {
        FolderReference.Parse(segment).ShouldBe(new FolderRef.ById(new FolderId(segment)));
    }
}

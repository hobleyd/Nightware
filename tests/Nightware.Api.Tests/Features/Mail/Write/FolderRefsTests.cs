using Nightware.Api.Features.Mail.Write;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Api.Tests.Features.Mail.Write;

public sealed class FolderRefsTests
{
    [Theory]
    [InlineData("inbox", WellKnownFolder.Inbox)]
    [InlineData("SentItems", WellKnownFolder.SentItems)]
    [InlineData("drafts", WellKnownFolder.Drafts)]
    [InlineData("deleteditems", WellKnownFolder.DeletedItems)]
    [InlineData("JunkEmail", WellKnownFolder.JunkEmail)]
    [InlineData("archive", WellKnownFolder.Archive)]
    [InlineData("outbox", WellKnownFolder.Outbox)]
    [InlineData(" msgfolderroot ", WellKnownFolder.MsgFolderRoot)]
    public void Well_known_names_are_case_insensitive(string value, WellKnownFolder expected)
    {
        FolderRefs.Parse(value, "destinationId").Value.ShouldBe(new FolderRef.WellKnown(expected));
    }

    [Fact]
    public void Anything_else_is_an_opaque_id()
    {
        FolderRefs.Parse("mbx-7", "destinationId").Value.ShouldBe(new FolderRef.ById(new FolderId("mbx-7")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void A_missing_value_is_a_validation_error_naming_the_target(string? value)
    {
        var parsed = FolderRefs.Parse(value, "destinationId");

        parsed.Error!.Kind.ShouldBe(ErrorKind.Validation);
        parsed.Error.Target.ShouldBe("destinationId");
    }
}

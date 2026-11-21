using Nightware.Application.Common.Query;
using Nightware.Application.Contacts;
using Nightware.Conformance.Tests.Harness;
using Nightware.Domain.Contacts;
using Nightware.Domain.Kernel;

namespace Nightware.Conformance.Tests.Arrangements;

/// <summary>Phase 14 contact folders and both deltas (ADR 0006 D7), against captures from Microsoft 365.</summary>
public sealed class ContactFolderArrangements : IConformanceArrangements
{
    private static readonly MailboxRef Me = new("me@example.com");
    private const string FolderId = "AAMk-id-0001=";
    private static readonly ContactFolder Folder = new(new ContactFolderId(FolderId), "Nightware 12fa34", new ContactFolderId("AAMk-parentFolderId-0001="));

    private static readonly Contact Sam = new(new ContactId("AAMk-id-0002="), "Sam Folder")
    {
        CreatedDateTime = new DateTimeOffset(2026, 11, 20, 22, 39, 1, TimeSpan.Zero),
        LastModifiedDateTime = new DateTimeOffset(2026, 11, 20, 22, 39, 1, TimeSpan.Zero),
        ChangeKey = "AAMk-changeKey-0001=",
        ParentFolderId = new ContactFolderId(FolderId),
        GivenName = "Sam",
        Surname = "Folder",
        EmailAddresses = [new EmailAddress("sam.folder@example.com", "Sam Folder")],
    };

    private static void Known(PortFakes fakes) =>
        fakes.For<IContactFolderStore>().GetAsync(Me, FolderId, Arg.Any<CancellationToken>()).Returns(Folder);

    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        yield return new ConformanceArrangement("contacts.folders.list", static fakes =>
            fakes.For<IContactFolderStore>().ListAsync(Me, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<ContactFolder>>.Success([])));

        yield return new ConformanceArrangement("contacts.folders.get", Known);

        yield return new ConformanceArrangement("contacts.folders.create", static fakes =>
            fakes.For<IContactFolderStore>().CreateAsync(Me, "Nightware 12fa34", Arg.Any<CancellationToken>()).Returns(Folder));

        yield return new ConformanceArrangement("contacts.folders.update", static fakes =>
        {
            Known(fakes);
            fakes.For<IContactFolderStore>().RenameAsync(Me, FolderId, "Nightware 12fa34 renamed", Arg.Any<CancellationToken>())
                .Returns(Folder with { DisplayName = "Nightware 12fa34 renamed" });
        });

        yield return new ConformanceArrangement("contacts.folders.delete", static fakes =>
        {
            Known(fakes);
            fakes.For<IContactFolderStore>().DeleteAsync(Me, FolderId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        });

        yield return new ConformanceArrangement("contacts.folders.child-folders.list", Known);

        yield return new ConformanceArrangement("contacts.folders.contacts.list", static fakes =>
        {
            Known(fakes);
            fakes.For<IContactStore>().ListInAsync(Me, Folder.Id, Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>()).Returns(new Page<Contact>([Sam]));
        });

        yield return new ConformanceArrangement("contacts.folders.contacts.create", static fakes =>
        {
            Known(fakes);
            fakes.For<IContactStore>()
                .CreateAsync(Me, Folder.Id, Arg.Is<ContactDraft>(d => d.GivenName == "Sam" && d.Surname == "Folder"), Arg.Any<CancellationToken>())
                .Returns(Sam);
        });

        yield return new ConformanceArrangement("contacts.delta", static fakes =>
        {
            Known(fakes);
            fakes.For<IContactDelta>().DeltaAsync(Me, Folder.Id, null, Arg.Any<CancellationToken>())
                .Returns(DeltaPage<Contact>.Done([], new DeltaToken("initial")));
        });

        // The capture follows Graph's deltaLink, which names the folder in key form: contactFolders('…').
        yield return new ConformanceArrangement("contacts.delta-changes", static fakes =>
        {
            Known(fakes);
            fakes.For<IContactDelta>().DeltaAsync(Me, Folder.Id, new DeltaToken("initial"), Arg.Any<CancellationToken>())
                .Returns(DeltaPage<Contact>.Done([new DeltaEntry<Contact>.Upsert(Sam)], new DeltaToken("next")));
        })
        {
            RequestToken = new DeltaToken("initial"),
        };

        yield return new ConformanceArrangement("contacts.folders.delta", static fakes =>
            fakes.For<IContactFolderStore>().ListAsync(Me, Arg.Any<CancellationToken>())
                .Returns(Result<IReadOnlyList<ContactFolder>>.Success([Folder with { DisplayName = "Nightware 12fa34 renamed" }])));
    }
}

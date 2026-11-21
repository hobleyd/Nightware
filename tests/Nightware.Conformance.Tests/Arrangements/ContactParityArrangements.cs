using Nightware.Application.Common.Query;
using Nightware.Application.Contacts;
using Nightware.Conformance.Tests.Harness;
using Nightware.Domain.Contacts;
using Nightware.Domain.Kernel;

namespace Nightware.Conformance.Tests.Arrangements;

/// <summary>
/// Phase 14 contact read/write and photo (ADR 0006 D7), against captures from Microsoft 365. Every member Graph
/// returns is mapped, so the bodies are compared whole: a forgotten mapping fails here.
/// </summary>
public sealed class ContactParityArrangements : IConformanceArrangements
{
    private static readonly MailboxRef Me = new("me@example.com");
    private static readonly ContactId PatId = new("AAMk-id-0003=");
    private static readonly DateTimeOffset Created = new(2026, 11, 20, 22, 39, 2, TimeSpan.Zero);

    /// <summary>The captured contact as first created.</summary>
    private static readonly Contact Pat = new(PatId, "Dr Pat Q Capture")
    {
        CreatedDateTime = Created,
        LastModifiedDateTime = Created,
        ChangeKey = "AAMk-changeKey-0002=",
        ParentFolderId = new ContactFolderId("AAMk-parentFolderId-0001="),
        Categories = ["Nightware 12fa34"],
        Birthday = new DateOnly(1990, 5, 17),
        GivenName = "Pat",
        MiddleName = "Q",
        NickName = "Patty",
        Surname = "Capture",
        Title = "Dr",
        ImAddresses = ["pat@im.example.com"],
        JobTitle = "Engineer",
        CompanyName = "Example Ltd",
        Department = "R&D",
        BusinessHomePage = "https://example.com/pat",
        HomePhones = ["+61 2 5550 0101"],
        MobilePhone = "+61 400 555 010",
        BusinessPhones = ["+61 2 5550 0100"],
        PersonalNotes = "Nightware capture 12fa34",
        EmailAddresses = [new EmailAddress("pat.capture@example.com", "Pat Capture")],
        HomeAddress = new PhysicalAddress("2 Sample Rd", "Sydney", "NSW", "2001", "Australia"),
        BusinessAddress = new PhysicalAddress("1 Example St", "Sydney", "NSW", "2000", "Australia"),
    };

    /// <summary>After the captured PATCH.</summary>
    private static readonly Contact Patched = Pat with { ChangeKey = "AAMk-changeKey-0003=", CompanyName = "Example Pty Ltd", BusinessPhones = ["+61 2 5550 0199"] };

    /// <summary>The capture tool's 1×1 JPEG (the same bytes the photo fixtures carry).</summary>
    private static byte[] Photo => Convert.FromBase64String(GraphFixture.Load("Fixtures/contacts.photo-get.json").Response.BodyBase64!);

    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        yield return new ConformanceArrangement("contacts.create", static fakes =>
            fakes.For<IContactStore>()
                .CreateAsync(Me, null, Arg.Is<ContactDraft>(d => d.GivenName == "Pat" && d.MiddleName == "Q" && d.Title == "Dr" && d.Department == "R&D"
                    && d.Birthday == new DateOnly(1990, 5, 17) && d.EmailAddresses!.Single() == new EmailAddress("pat.capture@example.com", "Pat Capture")
                    && d.Categories!.Single() == "Nightware 12fa34" && d.BusinessHomePage == "https://example.com/pat" && d.HomeAddress!.PostalCode == "2001"), Arg.Any<CancellationToken>())
                .Returns(Pat));

        yield return new ConformanceArrangement("contacts.get", static fakes =>
            fakes.For<IContactStore>().GetAsync(Me, PatId, Arg.Any<CancellationToken>()).Returns(Pat));

        yield return new ConformanceArrangement("contacts.update", static fakes =>
            fakes.For<IContactStore>()
                .UpdateAsync(Me, PatId, Arg.Is<ContactDraft>(d => d.CompanyName == "Example Pty Ltd" && d.BusinessPhones!.Single() == "+61 2 5550 0199" && d.GivenName == null), Arg.Any<CancellationToken>())
                .Returns(Patched));

        yield return new ConformanceArrangement("contacts.list-full", static fakes =>
            fakes.For<IContactStore>()
                .ListAsync(Me, Arg.Is<ItemQuery>(q => q.Top == 5), Arg.Any<CancellationToken>())
                .Returns(new Page<Contact>([Patched with { ChangeKey = "AAMk-changeKey-0004=", LastModifiedDateTime = Created.AddSeconds(1) }])));

        yield return new ConformanceArrangement("contacts.delete", static fakes =>
            fakes.For<IContactStore>().DeleteAsync(Me, PatId, Arg.Any<CancellationToken>()).Returns(Result.Success()));

        // The harness replays the captured upload as its length placeholder, so the bytes are not checked here.
        yield return new ConformanceArrangement("contacts.photo-put", static fakes =>
            fakes.For<IContactStore>().SetPhotoAsync(Me, PatId, Arg.Any<byte[]>(), "image/jpeg", Arg.Any<CancellationToken>()).Returns(Result.Success()));

        yield return new ConformanceArrangement("contacts.photo-get", static fakes =>
            fakes.For<IContactStore>().GetPhotoAsync(Me, PatId, Arg.Any<CancellationToken>())
                .Returns(new BlobContent("image/jpeg", null, _ => Task.FromResult<Stream>(new MemoryStream(Photo)))));
    }
}

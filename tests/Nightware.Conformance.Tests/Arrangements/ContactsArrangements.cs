using Nightware.Application.Common.Query;
using Nightware.Application.Contacts;
using Nightware.Conformance.Tests.Harness;
using Nightware.Domain.Contacts;
using Nightware.Domain.Kernel;

namespace Nightware.Conformance.Tests.Arrangements;

/// <summary>Phase 5A personal contacts (read-only).</summary>
public sealed class ContactsArrangements : IConformanceArrangements
{
    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        yield return new ConformanceArrangement("contacts.list", static fakes =>
            fakes.For<IContactStore>()
                .ListAsync(new MailboxRef("me@example.com"), Arg.Is<ItemQuery>(q => q.Top == 999), Arg.Any<CancellationToken>())
                .Returns(new Page<Contact>(
                [
                    new Contact(new ContactId("AAMkAGVmMDEzMTM4-contact-0001="), "Pat Contact")
                    {
                        EmailAddresses =
                        [
                            new EmailAddress("pat@example.com", "Pat Contact"),
                            new EmailAddress("pat.home@example.net", "pat.home@example.net"),
                        ],
                    },
                ])));
    }
}

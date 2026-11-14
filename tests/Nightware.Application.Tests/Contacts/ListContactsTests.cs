using Nightware.Application.Common.Query;
using Nightware.Application.Contacts;
using Nightware.Domain.Contacts;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Contacts;

public sealed class ListContactsTests
{
    private static readonly MailboxRef Me = new("me@example.com");

    [Fact]
    public async Task Passes_mailbox_and_query_to_the_store()
    {
        var store = Substitute.For<IContactStore>();
        var query = new ItemQuery { Top = 999 };
        var page = new Page<Contact>([new Contact(new ContactId("c1"), "Pat")], "next");
        store.ListAsync(Me, query, Arg.Any<CancellationToken>()).Returns(page);

        (await new ListContacts(store).ExecuteAsync(Me, query, TestContext.Current.CancellationToken)).Value.ShouldBe(page);
    }

    [Fact]
    public async Task Store_failures_pass_through()
    {
        var store = Substitute.For<IContactStore>();
        store.ListAsync(Me, Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>()).Returns(Error.Forbidden("no"));

        (await new ListContacts(store).ExecuteAsync(Me, ItemQuery.Default, TestContext.Current.CancellationToken)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }
}

using System.Net;

using Microsoft.Extensions.Logging.Abstractions;

using Nightware.Adapters.Dav.Contacts;
using Nightware.Adapters.Dav.Contacts.VCard;
using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Core.Collections;
using Nightware.Adapters.Dav.Core.Discovery;
using Nightware.Application.Common.Query;
using Nightware.Domain.Contacts;
using Nightware.Domain.Kernel;

using WireMock.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Nightware.Adapters.Dav.Tests.Contacts;

public sealed class DavContactStoreTests : IDisposable
{
    private const string Home = "/remote.php/dav/addressbooks/users/alice/";
    private const string Book = Home + "contacts/";
    private const string Team = Home + "team_shared_by_bob/";
    private static readonly MailboxRef Alice = new("alice@nightware.localhost");
    private static readonly DavPrincipal Principal = new("/remote.php/dav/principals/users/alice/", true) { AddressBookHomeSet = Home };

    private readonly DavTestServer _dav = new();
    private readonly IDavPrincipalDiscovery _discovery = Substitute.For<IDavPrincipalDiscovery>();
    private readonly IDavCollectionCatalog _catalog = Substitute.For<IDavCollectionCatalog>();

    public DavContactStoreTests()
    {
        _discovery.DiscoverAsync(Alice, Arg.Any<CancellationToken>()).Returns(Principal);
        GivenBooks(new DavAddressBook(Book, "Contacts"));
    }

    public void Dispose() => _dav.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DavContactStore Store => new(_discovery, _catalog, _dav.Client, _dav.Paths, new VCardCodec(_dav.Time), NullLogger<DavContactStore>.Instance);

    private void GivenBooks(params DavAddressBook[] books) =>
        _catalog.ListAddressBooksAsync(Principal, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<DavAddressBook>>.Success(books));

    private void GivenReport(string path, string bodyContains, string fixture, int status = 207) =>
        _dav.Server.Given(Request.Create().WithPath(path).UsingMethod("REPORT").WithBody(b => b?.Contains(bodyContains, StringComparison.Ordinal) == true))
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/xml; charset=utf-8")
                .WithBody(fixture.Length == 0 ? string.Empty : Fixture.Read(fixture)));

    private void GivenContactsBook()
    {
        GivenReport(Book, "addressbook-query", "Contacts/query-contacts.xml");
        GivenReport(Book, "addressbook-multiget", "Contacts/multiget-contacts.xml");
    }

    private ILogEntry[] Reports(string kind) =>
        [.. _dav.Server.LogEntries.Where(e => e.RequestMessage!.Method == "REPORT" && e.RequestMessage.Body!.Contains(kind, StringComparison.Ordinal))];

    [Fact]
    public async Task Lists_cards_ordered_by_href_with_ids_from_their_hrefs()
    {
        GivenContactsBook();

        var page = (await Store.ListAsync(Alice, new ItemQuery { Top = 999 }, Ct)).Value;

        page.Items.Select(static c => c.DisplayName).ShouldBe(["Jane Smith", "Pat Contact"], "the unparsable card is skipped");
        page.SkipToken.ShouldBeNull();
        var jane = page.Items[0];
        jane.Id.ShouldBe(DavItemId.Resource("addressbooks/users/alice/contacts/a-jane.vcf").ToContactId());
        jane.CompanyName.ShouldBe("Example Org");
        jane.EmailAddresses.Select(static e => e.Address).ShouldBe(["jane@example.net", "jane.smith@example.org"], "the preferred address comes first");
        page.Items[1].EmailAddresses.ShouldBe([new EmailAddress("pat@example.com", "Pat Contact")]);
    }

    [Fact]
    public async Task Lists_hrefs_without_data_then_multigets_server_absolute_hrefs()
    {
        GivenContactsBook();

        await Store.ListAsync(Alice, new ItemQuery { Top = 999 }, Ct);

        var query = Reports("addressbook-query").ShouldHaveSingleItem().RequestMessage!;
        query.Headers!["Depth"].ShouldBe(["1"]);
        query.Body!.ShouldNotContain("address-data");
        var multiget = Reports("addressbook-multiget").ShouldHaveSingleItem().RequestMessage!;
        multiget.Body!.ShouldContain("<d:href>/remote.php/dav/addressbooks/users/alice/contacts/a-jane.vcf</d:href>");
        multiget.Body!.ShouldContain("address-data");
    }

    [Fact]
    public async Task Pages_by_offset_across_address_books()
    {
        GivenBooks(new DavAddressBook(Team, "Team") { IsShared = true }, new DavAddressBook(Book, "Contacts"));
        GivenContactsBook();
        GivenReport(Team, "addressbook-query", "Contacts/query-team.xml");
        GivenReport(Team, "addressbook-multiget", "Contacts/multiget-team.xml");

        var first = (await Store.ListAsync(Alice, new ItemQuery { Top = 3 }, Ct)).Value;
        first.Items.Select(static c => c.DisplayName).ShouldBe(["Jane Smith", "Pat Contact"], "books in href order; the broken card still takes its slot");
        first.SkipToken.ShouldNotBeNull();

        var second = (await Store.ListAsync(Alice, new ItemQuery { Top = 3, SkipToken = first.SkipToken }, Ct)).Value;
        second.Items.ShouldHaveSingleItem().DisplayName.ShouldBe("Sam Attendee");
        second.Items[0].Id.ShouldBe(DavItemId.Resource("addressbooks/users/alice/team_shared_by_bob/0f%20sam.vcf").ToContactId());
        second.SkipToken.ShouldBeNull();
    }

    [Fact]
    public async Task Only_the_pages_slice_is_fetched()
    {
        GivenContactsBook();

        var page = (await Store.ListAsync(Alice, new ItemQuery { Top = 1 }, Ct)).Value;

        page.Items.ShouldHaveSingleItem().DisplayName.ShouldBe("Jane Smith");
        var multiget = Reports("addressbook-multiget").ShouldHaveSingleItem().RequestMessage!.Body!;
        multiget.ShouldContain("a-jane.vcf");
        multiget.ShouldNotContain("b-pat.vcf");
    }

    [Fact]
    public async Task Pages_default_to_graphs_size()
    {
        GivenContactsBook();

        (await Store.ListAsync(Alice, ItemQuery.Default, Ct)).Value.SkipToken.ShouldBeNull();
    }

    [Fact]
    public async Task Generated_address_books_are_not_personal_contacts()
    {
        GivenBooks(
            new DavAddressBook(Home + "z-server-generated--system/", "Accounts"),
            new DavAddressBook(Home + "z-app-generated--contactsinteraction--recent/", "Recently contacted"),
            new DavAddressBook(Book, "Contacts"));
        GivenContactsBook();

        (await Store.ListAsync(Alice, new ItemQuery { Top = 999 }, Ct)).Value.Items.Count.ShouldBe(2);

        Reports("addressbook-query").ShouldHaveSingleItem().RequestMessage!.Path.ShouldBe(Book);
    }

    [Fact]
    public async Task An_empty_mailbox_lists_nothing_without_fetching()
    {
        GivenBooks();

        var page = (await Store.ListAsync(Alice, new ItemQuery { Top = 999 }, Ct)).Value;

        page.Items.ShouldBeEmpty();
        page.SkipToken.ShouldBeNull();
        _dav.Server.LogEntries.ShouldBeEmpty();
    }

    public static TheoryData<ItemQuery, string> Unsupported => new()
    {
        { new ItemQuery { Filter = new FilterNode.Compare("displayName", CompareOp.Eq, new FilterValue.Text("x")) }, "$filter" },
        { new ItemQuery { Search = new SearchQuery([]) }, "$search" },
        { new ItemQuery { OrderBy = [new OrderBy("displayName", Nightware.Application.Common.Query.SortDirection.Ascending)] }, "$orderby" },
        { new ItemQuery { Skip = 5 }, "$skip" },
        { new ItemQuery { Expand = [new ExpandClause("photo")] }, "$expand" },
        { new ItemQuery { SkipToken = "not-a-token!" }, "$skiptoken" },
        { new ItemQuery { SkipToken = "eDE6Mw" }, "$skiptoken" }, // "x1:3"
    };

    [Theory]
    [MemberData(nameof(Unsupported))]
    public async Task Rejects_query_options_carddav_cannot_answer(ItemQuery query, string option)
    {
        var error = (await Store.ListAsync(Alice, query, Ct)).Error!;

        error.Kind.ShouldBe(ErrorKind.Validation);
        error.Target.ShouldBe(option);
        await _discovery.DidNotReceive().DiscoverAsync(Arg.Any<MailboxRef>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(403, ErrorKind.Forbidden)]
    [InlineData(404, ErrorKind.NotFound)]
    [InlineData(503, ErrorKind.BackendUnavailable)]
    public async Task Backend_failures_keep_their_kind(int status, ErrorKind kind)
    {
        _dav.Server.Given(Request.Create().WithPath(Book).UsingMethod("REPORT")).RespondWith(Response.Create().WithStatusCode(status));

        (await Store.ListAsync(Alice, ItemQuery.Default, Ct)).Error!.Kind.ShouldBe(kind);
    }

    [Fact]
    public async Task Discovery_and_catalog_failures_pass_through()
    {
        _discovery.DiscoverAsync(new MailboxRef("shared@example.com"), Arg.Any<CancellationToken>()).Returns(Error.NotFound("no principal"));
        (await Store.ListAsync(new MailboxRef("shared@example.com"), ItemQuery.Default, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);

        _catalog.ListAddressBooksAsync(Principal, Arg.Any<CancellationToken>()).Returns(Error.Forbidden("no"));
        (await Store.ListAsync(Alice, ItemQuery.Default, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task Gets_one_card_of_the_mailbox()
    {
        _dav.Server.Given(Request.Create().WithPath(Book + "b-pat.vcf").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "text/vcard").WithBody("BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Pat Contact\r\nEMAIL:pat@example.com\r\nEND:VCARD\r\n"));
        var id = DavItemId.Resource("addressbooks/users/alice/contacts/b-pat.vcf").ToContactId();

        var pat = (await Store.GetAsync(Alice, id, Ct)).Value;

        pat.Id.ShouldBe(id);
        pat.DisplayName.ShouldBe("Pat Contact");
    }

    [Theory]
    [InlineData("addressbooks/users/bob/contacts/x.vcf")]
    [InlineData("calendars/alice/personal/x.ics")]
    public async Task A_card_outside_the_mailboxs_address_books_is_not_found(string href)
    {
        (await Store.GetAsync(Alice, DavItemId.Resource(href).ToContactId(), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);

        _dav.Server.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_tampered_id_is_not_found()
    {
        (await Store.GetAsync(Alice, new ContactId("garbage"), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Writes_are_unsupported_because_contacts_are_read_only()
    {
        var id = DavItemId.Resource("addressbooks/users/alice/contacts/b-pat.vcf").ToContactId();

        (await Store.CreateAsync(Alice, new ContactDraft(), Ct)).Error!.Kind.ShouldBe(ErrorKind.Unsupported);
        (await Store.UpdateAsync(Alice, id, new ContactDraft(), Ct)).Error!.Kind.ShouldBe(ErrorKind.Unsupported);
        (await Store.DeleteAsync(Alice, id, Ct)).Error!.Kind.ShouldBe(ErrorKind.Unsupported);
        _dav.Server.LogEntries.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("/remote.php/dav/addressbooks/users/alice/z-server-generated--system/", true)]
    [InlineData("/remote.php/dav/addressbooks/users/alice/z-app-generated--contactsinteraction--recent", true)]
    [InlineData("/remote.php/dav/addressbooks/users/alice/contacts/", false)]
    [InlineData("/remote.php/dav/addressbooks/users/alice/my-z-server-generated--x/", false)]
    public void Recognises_generated_address_books(string href, bool generated) =>
        DavContactStore.IsGenerated(new DavAddressBook(href, "x")).ShouldBe(generated);
}

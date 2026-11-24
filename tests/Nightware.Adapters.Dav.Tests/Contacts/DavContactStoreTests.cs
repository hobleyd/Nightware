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
    public async Task Count_is_every_card_across_the_books_not_the_page()
    {
        GivenBooks(new DavAddressBook(Team, "Team") { IsShared = true }, new DavAddressBook(Book, "Contacts"));
        GivenContactsBook();
        GivenReport(Team, "addressbook-query", "Contacts/query-team.xml");
        GivenReport(Team, "addressbook-multiget", "Contacts/multiget-team.xml");

        var page = (await Store.ListAsync(Alice, new ItemQuery { Top = 3, Count = true }, Ct)).Value;

        page.Total.ShouldBe(4, "three in the first page's slots (one card is broken) plus one more");
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

    private const string Pat = "BEGIN:VCARD\r\nVERSION:3.0\r\nUID:pat\r\nFN:Pat Contact\r\nN:Contact;Pat;;;\r\nEMAIL:pat@example.com\r\nX-CUSTOM:kept\r\nEND:VCARD\r\n";
    private static readonly ContactId PatId = DavItemId.Resource("addressbooks/users/alice/contacts/b-pat.vcf").ToContactId();

    private void GivenCard(string path, string body, string etag = "\"e1\"") =>
        _dav.Server.Given(Request.Create().WithPath(path).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "text/vcard").WithHeader("ETag", etag).WithBody(body));

    private void GivenPut(string path, int status = 204) =>
        _dav.Server.Given(Request.Create().WithPath(path).UsingPut()).RespondWith(Response.Create().WithStatusCode(status).WithHeader("ETag", "\"e2\""));

    private ILogEntry Put() => _dav.Server.LogEntries.Single(e => e.RequestMessage!.Method == "PUT");

    [Fact]
    public async Task A_contact_is_its_books_child_with_the_etag_as_change_key()
    {
        GivenCard(Book + "b-pat.vcf", Pat);

        var pat = (await Store.GetAsync(Alice, PatId, Ct)).Value;

        pat.ParentFolderId.ShouldBe(DavContactStore.FolderOf("addressbooks/users/alice/contacts/"));
        pat.ChangeKey.ShouldBe("\"e1\"");
    }

    [Fact]
    public async Task Creating_puts_a_new_card_in_the_default_book_only_if_absent()
    {
        GivenBooks(new DavAddressBook(Team, "Team") { IsShared = true }, new DavAddressBook(Book, "Contacts"));
        _dav.Server.Given(Request.Create().WithPath(new WireMock.Matchers.WildcardMatcher(Book + "*.vcf")).UsingPut()).RespondWith(Response.Create().WithStatusCode(201));
        _dav.Server.Given(Request.Create().WithPath(new WireMock.Matchers.WildcardMatcher(Book + "*.vcf")).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("ETag", "\"n1\"").WithBody("BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Dr Ann B New\r\nN:New;Ann;B;Dr;\r\nEND:VCARD\r\n"));

        var created = (await Store.CreateAsync(Alice, null, new ContactDraft { GivenName = "Ann", Surname = "New", MiddleName = "B", Title = "Dr" }, Ct)).Value;

        created.DisplayName.ShouldBe("Dr Ann B New");
        var put = Put().RequestMessage!;
        put.Path.ShouldStartWith(Book);
        put.Headers!["If-None-Match"].ShouldBe(["*"]);
        put.Body!.ShouldContain("N:New;Ann;B;Dr;");
        created.Id.ShouldBe(DavItemId.Resource(put.Path[_dav.Root.Length..].TrimStart('/')).ToContactId());
    }

    [Fact]
    public async Task Creating_in_a_folder_that_is_not_the_mailboxs_is_not_found()
    {
        (await Store.CreateAsync(Alice, new ContactFolderId("nope"), new ContactDraft { GivenName = "x" }, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _dav.Server.LogEntries.ShouldNotContain(e => e.RequestMessage!.Method == "PUT");
    }

    [Fact]
    public async Task Updating_applies_the_patch_on_the_cards_etag_and_keeps_the_rest()
    {
        GivenCard(Book + "b-pat.vcf", Pat);
        GivenPut(Book + "b-pat.vcf");

        (await Store.UpdateAsync(Alice, PatId, new ContactDraft { CompanyName = "Example Pty Ltd" }, Ct)).IsSuccess.ShouldBeTrue();

        var put = Put().RequestMessage!;
        put.Headers!["If-Match"].ShouldBe(["\"e1\""]);
        put.Body!.ShouldContain("ORG:Example Pty Ltd");
        put.Body!.ShouldContain("X-CUSTOM:kept");
    }

    [Fact]
    public async Task A_concurrent_change_fails_the_update()
    {
        GivenCard(Book + "b-pat.vcf", Pat);
        GivenPut(Book + "b-pat.vcf", 412);

        (await Store.UpdateAsync(Alice, PatId, new ContactDraft { JobTitle = "x" }, Ct)).Error!.Kind.ShouldBe(ErrorKind.PreconditionFailed);
    }

    [Fact]
    public async Task Deleting_removes_the_card()
    {
        _dav.Server.Given(Request.Create().WithPath(Book + "b-pat.vcf").UsingDelete()).RespondWith(Response.Create().WithStatusCode(204));

        (await Store.DeleteAsync(Alice, PatId, Ct)).IsSuccess.ShouldBeTrue();
        (await Store.DeleteAsync(Alice, DavItemId.Resource("addressbooks/users/bob/contacts/x.vcf").ToContactId(), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task A_photo_is_written_as_vcard_3_base64_and_read_back()
    {
        GivenCard(Book + "b-pat.vcf", Pat);
        GivenPut(Book + "b-pat.vcf");
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xD9];

        (await Store.SetPhotoAsync(Alice, PatId, jpeg, "image/jpeg", Ct)).IsSuccess.ShouldBeTrue();
        var written = Put().RequestMessage!.Body!;
        written.ShouldContain("PHOTO;ENCODING=b;TYPE=JPEG:" + Convert.ToBase64String(jpeg));

        _dav.Server.Reset();
        GivenCard(Book + "b-pat.vcf", written);
        var photo = (await Store.GetPhotoAsync(Alice, PatId, Ct)).Value;
        photo.ContentType.ShouldBe("image/jpeg");
        using var stream = await photo.Open(Ct);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, Ct);
        copy.ToArray().ShouldBe(jpeg);
    }

    [Fact]
    public async Task A_contact_without_a_photo_has_none()
    {
        GivenCard(Book + "b-pat.vcf", Pat);

        (await Store.GetPhotoAsync(Alice, PatId, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public void The_default_book_is_contacts_else_the_first_own_book()
    {
        DavContactStore.DefaultBook([new DavAddressBook(Team, "Team") { IsShared = true }, new DavAddressBook(Book, "Contacts")])!.Href.ShouldBe(Book);
        DavContactStore.DefaultBook([new DavAddressBook(Team, "Team") { IsShared = true }, new DavAddressBook(Home + "work/", "Work")])!.Href.ShouldBe(Home + "work/");
        DavContactStore.DefaultBook([new DavAddressBook(Team, "Team") { IsShared = true }]).ShouldBeNull();
    }

    [Theory]
    [InlineData("/remote.php/dav/addressbooks/users/alice/z-server-generated--system/", true)]
    [InlineData("/remote.php/dav/addressbooks/users/alice/z-app-generated--contactsinteraction--recent", true)]
    [InlineData("/remote.php/dav/addressbooks/users/alice/contacts/", false)]
    [InlineData("/remote.php/dav/addressbooks/users/alice/my-z-server-generated--x/", false)]
    public void Recognises_generated_address_books(string href, bool generated) =>
        DavContactStore.IsGenerated(new DavAddressBook(href, "x")).ShouldBe(generated);

    private static readonly MailboxRef Desk = new("desk@nightware.localhost");
    private const string DeskBook = Home + "contacts_shared_by_desk/";

    private void GivenDesk(params DavAddressBook[] books)
    {
        _discovery.DiscoverAsync(Desk, Arg.Any<CancellationToken>()).Returns(new DavPrincipal("/remote.php/dav/principals/users/desk/", false)
        {
            AddressBookHomeSet = "/remote.php/dav/addressbooks/users/desk/",
        });
        _discovery.CurrentUserAsync(Arg.Any<CancellationToken>()).Returns(Principal);
        GivenBooks(books);
    }

    [Fact]
    public async Task Another_mailboxs_contacts_are_the_books_it_shared_with_the_caller()
    {
        GivenDesk(
            new DavAddressBook(Book, "Contacts"),
            new DavAddressBook(Team, "Team") { IsShared = true, OwnerPrincipal = "principals/users/bob" },
            new DavAddressBook(DeskBook, "Contacts (Desk)") { IsShared = true, OwnerPrincipal = "principals/users/desk" });

        var books = (await Store.AddressBooksAsync(Desk, Ct)).Value;

        books.Select(static b => b.Href).ShouldBe([DeskBook]);
        (await Store.BookAsync(Desk, null, Ct)).Value.Href.ShouldBe(DeskBook, "a new contact goes into the shared book");
    }

    [Fact]
    public async Task A_card_of_another_mailbox_must_be_in_a_book_it_shared()
    {
        GivenDesk(new DavAddressBook(DeskBook, "Contacts (Desk)") { IsShared = true, OwnerPrincipal = "principals/users/desk" });
        _dav.Server.Given(Request.Create().WithPath(DeskBook + "c1.vcf").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "text/vcard").WithBody("BEGIN:VCARD\r\nVERSION:3.0\r\nFN:Sam Supplier\r\nEND:VCARD\r\n"));

        (await Store.GetAsync(Desk, DavItemId.Resource("addressbooks/users/alice/contacts_shared_by_desk/c1.vcf").ToContactId(), Ct)).Value.DisplayName.ShouldBe("Sam Supplier");
        (await Store.GetAsync(Desk, DavItemId.Resource("addressbooks/users/alice/contacts/b-pat.vcf").ToContactId(), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task A_mailbox_that_shared_nothing_with_the_caller_is_403()
    {
        GivenDesk(new DavAddressBook(Book, "Contacts"));

        (await Store.ListAsync(Desk, ItemQuery.Default, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }
}

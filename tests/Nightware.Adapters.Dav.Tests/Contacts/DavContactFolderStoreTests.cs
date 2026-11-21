using Microsoft.Extensions.Logging.Abstractions;

using Nightware.Adapters.Dav.Contacts;
using Nightware.Adapters.Dav.Contacts.VCard;
using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Core.Collections;
using Nightware.Adapters.Dav.Core.Discovery;
using Nightware.Application.Common.Query;
using Nightware.Domain.Kernel;

using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Nightware.Adapters.Dav.Tests.Contacts;

public sealed class DavContactFolderStoreTests : IDisposable
{
    private const string Home = "/remote.php/dav/addressbooks/users/alice/";
    private const string Book = Home + "contacts/";
    private const string Work = Home + "work/";
    private static readonly MailboxRef Alice = new("alice@nightware.localhost");
    private static readonly DavPrincipal Principal = new("/remote.php/dav/principals/users/alice/", true) { AddressBookHomeSet = Home };

    private readonly DavTestServer _dav = new();
    private readonly IDavPrincipalDiscovery _discovery = Substitute.For<IDavPrincipalDiscovery>();
    private readonly IDavCollectionCatalog _catalog = Substitute.For<IDavCollectionCatalog>();

    public DavContactFolderStoreTests()
    {
        _discovery.DiscoverAsync(Alice, Arg.Any<CancellationToken>()).Returns(Principal);
        _catalog.ListAddressBooksAsync(Principal, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<DavAddressBook>>.Success(
        [
            new DavAddressBook(Book, "Contacts"),
            new DavAddressBook(Work, "Work"),
            new DavAddressBook(Home + "z-server-generated--system/", "Accounts"),
        ]));
    }

    public void Dispose() => _dav.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DavContactStore Contacts => new(_discovery, _catalog, _dav.Client, _dav.Paths, new VCardCodec(_dav.Time), NullLogger<DavContactStore>.Instance);

    private DavContactFolderStore Store => new(Contacts, _discovery, _dav.Client, _dav.Paths);

    private string IdOf(string href) => DavContactStore.FolderOf(_dav.Paths.ToRelative(href)!).Value;

    [Fact]
    public async Task Folders_are_the_books_below_the_default_one()
    {
        var folder = (await Store.ListAsync(Alice, Ct)).Value.ShouldHaveSingleItem();

        folder.DisplayName.ShouldBe("Work");
        folder.Id.Value.ShouldBe(IdOf(Work));
        folder.ParentFolderId!.Value.Value.ShouldBe(IdOf(Book));
        folder.WellKnownName.ShouldBeNull();
    }

    [Fact]
    public async Task The_default_folder_is_found_by_id_or_well_known_name()
    {
        var byName = (await Store.GetAsync(Alice, "contacts", Ct)).Value;
        byName.WellKnownName.ShouldBe("contacts");
        byName.ParentFolderId.ShouldBeNull();
        (await Store.GetAsync(Alice, IdOf(Book), Ct)).Value.ShouldBe(byName);
        (await Store.GetAsync(Alice, "nope", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await Store.GetAsync(Alice, IdOf(Home + "z-server-generated--system/"), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Creating_is_an_extended_mkcol_of_an_address_book()
    {
        _dav.Server.Given(Request.Create().UsingMethod("MKCOL")).RespondWith(Response.Create().WithStatusCode(201));

        var created = (await Store.CreateAsync(Alice, "Friends", Ct)).Value;

        var mkcol = _dav.Server.LogEntries.Single().RequestMessage!;
        mkcol.Path.ShouldStartWith(Home + "nightware-");
        mkcol.Body!.ShouldContain("addressbook");
        mkcol.Body!.ShouldContain(">Friends</");
        created.Id.Value.ShouldBe(IdOf(mkcol.Path));
        created.ParentFolderId!.Value.Value.ShouldBe(IdOf(Book));
    }

    [Fact]
    public async Task A_name_already_taken_is_a_conflict()
    {
        (await Store.CreateAsync(Alice, "work", Ct)).Error!.Kind.ShouldBe(ErrorKind.Conflict);
        _dav.Server.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Renaming_proppatches_the_display_name()
    {
        _dav.Server.Given(Request.Create().WithPath(Work).UsingMethod("PROPPATCH")).RespondWith(Response.Create().WithStatusCode(207).WithBody(
            """<?xml version="1.0"?><d:multistatus xmlns:d="DAV:"><d:response><d:href>/x</d:href><d:propstat><d:prop><d:displayname/></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>"""));

        (await Store.RenameAsync(Alice, IdOf(Work), "Work 2", Ct)).Value.DisplayName.ShouldBe("Work 2");
        _dav.Server.LogEntries.Single().RequestMessage!.Body!.ShouldContain(">Work 2</");
    }

    [Fact]
    public async Task A_refused_rename_is_forbidden()
    {
        _dav.Server.Given(Request.Create().WithPath(Work).UsingMethod("PROPPATCH")).RespondWith(Response.Create().WithStatusCode(207).WithBody(
            """<?xml version="1.0"?><d:multistatus xmlns:d="DAV:"><d:response><d:href>/x</d:href><d:propstat><d:prop><d:displayname/></d:prop><d:status>HTTP/1.1 403 Forbidden</d:status></d:propstat></d:response></d:multistatus>"""));

        (await Store.RenameAsync(Alice, IdOf(Work), "x", Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task Deleting_removes_the_book()
    {
        _dav.Server.Given(Request.Create().WithPath(Work).UsingDelete()).RespondWith(Response.Create().WithStatusCode(204));

        (await Store.DeleteAsync(Alice, IdOf(Work), Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_folders_contacts_come_from_its_book_only()
    {
        _dav.Server.Given(Request.Create().WithPath(Work).UsingMethod("REPORT")).RespondWith(Response.Create().WithStatusCode(207).WithBody(
            """<?xml version="1.0"?><d:multistatus xmlns:d="DAV:"><d:response><d:href>/remote.php/dav/addressbooks/users/alice/work/</d:href><d:propstat><d:prop/><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>"""));

        (await Contacts.ListInAsync(Alice, new ContactFolderId(IdOf(Work)), ItemQuery.Default, Ct)).Value.Items.ShouldBeEmpty();
        _dav.Server.LogEntries.ShouldAllBe(e => e.RequestMessage!.Path == Work);
        (await Contacts.ListInAsync(Alice, new ContactFolderId("nope"), ItemQuery.Default, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }
}

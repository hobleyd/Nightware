using Microsoft.Extensions.Logging.Abstractions;

using Nightware.Adapters.Dav.Contacts;
using Nightware.Adapters.Dav.Contacts.VCard;
using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Core.Collections;
using Nightware.Adapters.Dav.Core.Discovery;
using Nightware.Domain.Contacts;
using Nightware.Domain.Kernel;

using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Nightware.Adapters.Dav.Tests.Contacts;

public sealed class DavContactDeltaTests : IDisposable
{
    private const string Home = "/remote.php/dav/addressbooks/users/alice/";
    private const string Book = Home + "contacts/";
    private const string Work = Home + "work/";
    private static readonly MailboxRef Alice = new("alice@nightware.localhost");
    private static readonly DavPrincipal Principal = new("/remote.php/dav/principals/users/alice/", true) { AddressBookHomeSet = Home };

    private readonly DavTestServer _dav = new();
    private readonly IDavPrincipalDiscovery _discovery = Substitute.For<IDavPrincipalDiscovery>();
    private readonly IDavCollectionCatalog _catalog = Substitute.For<IDavCollectionCatalog>();

    public DavContactDeltaTests()
    {
        _discovery.DiscoverAsync(Alice, Arg.Any<CancellationToken>()).Returns(Principal);
        _catalog.ListAddressBooksAsync(Principal, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<DavAddressBook>>.Success(
            [new DavAddressBook(Book, "Contacts"), new DavAddressBook(Work, "Work")]));
    }

    public void Dispose() => _dav.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DavContactDelta Delta => new(
        new DavContactStore(_discovery, _catalog, _dav.Client, _dav.Paths, new VCardCodec(_dav.Time), NullLogger<DavContactStore>.Instance), _dav.Client, _dav.Paths);

    private ContactFolderId WorkId => DavContactStore.FolderOf(_dav.Paths.ToRelative(Work)!);

    /// <param name="from">The sync token the request must carry; null for the initial sync (none).</param>
    private void GivenSync(string? from, string responses, string next) =>
        _dav.Server.Given(Request.Create().WithPath(Work).UsingMethod("REPORT").WithBody(b => b!.Contains("sync-collection", StringComparison.Ordinal)
                && (from is null ? !b.Contains("sync/", StringComparison.Ordinal) : b.Contains(from, StringComparison.Ordinal))))
            .RespondWith(Response.Create().WithStatusCode(207).WithBody(
                $"""<?xml version="1.0"?><d:multistatus xmlns:d="DAV:">{responses}<d:sync-token>{next}</d:sync-token></d:multistatus>"""));

    private void GivenMultiget(params (string Name, string Fn)[] cards) =>
        _dav.Server.Given(Request.Create().WithPath(Work).UsingMethod("REPORT").WithBody(b => b!.Contains("addressbook-multiget", StringComparison.Ordinal)))
            .RespondWith(Response.Create().WithStatusCode(207).WithBody(
                $"""<?xml version="1.0"?><d:multistatus xmlns:d="DAV:" xmlns:card="urn:ietf:params:xml:ns:carddav">{string.Concat(cards.Select(c =>
                    $"<d:response><d:href>{Work}{c.Name}</d:href><d:propstat><d:prop><d:getetag>\"e-{c.Name}\"</d:getetag><card:address-data>BEGIN:VCARD&#13;\nVERSION:3.0&#13;\nFN:{c.Fn}&#13;\nEND:VCARD&#13;\n</card:address-data></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>"))}</d:multistatus>"""));

    private static string Found(string href) =>
        $"<d:response><d:href>{href}</d:href><d:propstat><d:prop><d:getetag>\"1\"</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>";

    private static string Gone(string href) => $"<d:response><d:href>{href}</d:href><d:status>HTTP/1.1 404 Not Found</d:status></d:response>";

    [Fact]
    public async Task The_initial_sync_is_every_card_then_changes_and_removals_follow_the_token()
    {
        GivenSync(null, Found(Work) + Found(Work + "a.vcf") + Found(Work + "b.vcf"), "sync/5");
        GivenMultiget(("a.vcf", "Ann"), ("b.vcf", "Bo"));

        var initial = (await Delta.DeltaAsync(Alice, WorkId, null, Ct)).Value;

        initial.Entries.OfType<DeltaEntry<Contact>.Upsert>().Select(u => u.Item.DisplayName).ShouldBe(["Ann", "Bo"]);
        initial.Entries.OfType<DeltaEntry<Contact>.Upsert>().ShouldAllBe(u => u.Item.ParentFolderId == WorkId);
        var token = initial.Final!.Value;

        _dav.Server.Reset();
        GivenSync("sync/5", Found(Work + "a.vcf") + Gone(Work + "b.vcf"), "sync/6");
        GivenMultiget(("a.vcf", "Ann Edited"));

        var changes = (await Delta.DeltaAsync(Alice, WorkId, token, Ct)).Value;

        changes.Entries.OfType<DeltaEntry<Contact>.Upsert>().ShouldHaveSingleItem().Item.DisplayName.ShouldBe("Ann Edited");
        changes.Entries.OfType<DeltaEntry<Contact>.Removed>().ShouldHaveSingleItem().Id
            .ShouldBe(DavItemId.Resource(_dav.Paths.ToRelative(Work + "b.vcf")!).ToContactId().Value);
        changes.Final.ShouldNotBeNull();
    }

    [Fact]
    public async Task Nothing_changed_needs_no_multiget()
    {
        GivenSync(null, Found(Work), "sync/1");

        (await Delta.DeltaAsync(Alice, WorkId, null, Ct)).Value.Entries.ShouldBeEmpty();
        _dav.Server.LogEntries.ShouldNotContain(e => e.RequestMessage!.Body!.Contains("multiget", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_token_for_another_folder_or_garbage_is_gone()
    {
        GivenSync(null, Found(Work), "sync/1");
        var token = (await Delta.DeltaAsync(Alice, WorkId, null, Ct)).Value.Final!.Value;
        var contactsId = DavContactStore.FolderOf(_dav.Paths.ToRelative(Book)!);

        (await Delta.DeltaAsync(Alice, contactsId, token, Ct)).Error!.Kind.ShouldBe(ErrorKind.SyncStateGone);
        (await Delta.DeltaAsync(Alice, WorkId, new DeltaToken("!!"), Ct)).Error!.Kind.ShouldBe(ErrorKind.SyncStateGone);
        (await Delta.DeltaAsync(Alice, new ContactFolderId("nope"), null, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }
}

using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Core.Collections;
using Nightware.Adapters.Dav.Core.Discovery;
using Nightware.Domain.Kernel;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Nightware.Adapters.Dav.Tests.Core;

public sealed class DavCollectionCatalogTests : IDisposable
{
    private static readonly DavPrincipal Alice = new("/remote.php/dav/principals/users/alice/", true)
    {
        CalendarHomeSet = "/remote.php/dav/calendars/alice/",
        AddressBookHomeSet = "/remote.php/dav/addressbooks/users/alice/",
    };

    private readonly DavTestServer _dav = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private void Serve(string path, string fixture) =>
        _dav.Server.Given(Request.Create().WithPath(path).UsingMethod("PROPFIND"))
            .RespondWith(Response.Create().WithStatusCode(207).WithBody(Fixture.Read(fixture)));

    [Fact]
    public async Task Lists_calendars_with_components_colors_tokens_and_sharing()
    {
        Serve("/remote.php/dav/calendars/alice/", "propfind-calendar-home.xml");

        var calendars = (await new DavCollectionCatalog(_dav.Client).ListCalendarsAsync(Alice, Ct)).Value;

        calendars.Select(c => c.DisplayName).ShouldBe(["Personal", "Tasks", "Team calendar (Bob)"]);
        var personal = calendars[0];
        personal.Href.ShouldBe("/remote.php/dav/calendars/alice/personal/");
        personal.SupportsEvents.ShouldBeTrue();
        personal.SupportsTasks.ShouldBeFalse();
        personal.Color.ShouldBe("#0082c9");
        personal.CTag.ShouldBe("http://sabre.io/ns/sync/42");
        personal.SyncToken.ShouldBe("http://sabre.io/ns/sync/42");
        personal.IsShared.ShouldBeFalse();
        personal.IsReadOnly.ShouldBeFalse();

        var tasks = calendars[1];
        tasks.SupportsTasks.ShouldBeTrue();
        tasks.SupportsEvents.ShouldBeFalse();

        var shared = calendars[2];
        shared.IsShared.ShouldBeTrue();
        shared.IsReadOnly.ShouldBeTrue();
        shared.OwnerPrincipal.ShouldBe("principals/users/bob");
        shared.OwnerDisplayName.ShouldBe("Bob Builder");
        shared.Components.ShouldBe(["VEVENT", "VTODO"], ignoreOrder: true);

        var body = System.Xml.Linq.XDocument.Parse(_dav.Server.LogEntries.Single().RequestMessage!.Body!);
        body.Descendants(DavNames.SupportedCalendarComponentSet).ShouldHaveSingleItem();
        body.Descendants(DavNames.SyncToken).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Lists_address_books()
    {
        Serve("/remote.php/dav/addressbooks/users/alice/", "propfind-addressbook-home.xml");

        var books = (await new DavCollectionCatalog(_dav.Client).ListAddressBooksAsync(Alice, Ct)).Value;

        books.Count.ShouldBe(2);
        books[0].DisplayName.ShouldBe("Contacts");
        books[0].SyncToken.ShouldBe("http://sabre.io/ns/sync/12");
        books[0].IsShared.ShouldBeFalse();
        books[1].IsShared.ShouldBeTrue();
        books[1].IsReadOnly.ShouldBeTrue();
    }

    [Fact]
    public async Task Missing_home_sets_are_not_found()
    {
        var bare = new DavPrincipal("/remote.php/dav/principals/calendar-rooms/x/", false);
        var catalog = new DavCollectionCatalog(_dav.Client);
        (await catalog.ListCalendarsAsync(bare, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await catalog.ListAddressBooksAsync(bare, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Http_failures_propagate()
    {
        _dav.Server.Given(Request.Create().WithPath("/remote.php/dav/calendars/alice/").UsingMethod("PROPFIND")).RespondWith(Response.Create().WithStatusCode(403));
        (await new DavCollectionCatalog(_dav.Client).ListCalendarsAsync(Alice, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    public void Dispose() => _dav.Dispose();
}

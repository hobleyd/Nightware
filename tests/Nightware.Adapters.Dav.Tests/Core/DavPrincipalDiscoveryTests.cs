using System.Xml.Linq;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Core.Discovery;
using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Nightware.Adapters.Dav.Tests.Core;

public sealed class DavPrincipalDiscoveryTests : IDisposable
{
    private readonly DavTestServer _dav = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DavPrincipalDiscovery Discovery() =>
        new(_dav.Client, _dav.Credentials, _cache, _dav.Paths, Options.Create(_dav.Options));

    private void Serve(string method, string path, string fixture, int status = 207) =>
        _dav.Server.Given(Request.Create().WithPath(path).UsingMethod(method))
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/xml").WithBody(Fixture.Read(fixture)));

    private void ServeSelf()
    {
        Serve("PROPFIND", "/remote.php/dav/", "propfind-root-current-user.xml");
        Serve("PROPFIND", "/remote.php/dav/principals/users/alice/", "propfind-principal-alice.xml");
    }

    private int Count(string method) => _dav.Server.LogEntries.Count(e => e.RequestMessage!.Method == method);

    [Fact]
    public async Task Discovers_the_callers_own_principal()
    {
        ServeSelf();

        var result = await Discovery().DiscoverAsync(new MailboxRef("ALICE@example.com"), Ct);

        var alice = result.Value;
        alice.IsCurrentUser.ShouldBeTrue();
        alice.Href.ShouldBe("/remote.php/dav/principals/users/alice/");
        alice.DisplayName.ShouldBe("Alice Example");
        alice.CalendarHomeSet.ShouldBe("/remote.php/dav/calendars/alice/");
        alice.AddressBookHomeSet.ShouldBe("/remote.php/dav/addressbooks/users/alice/");
        alice.ScheduleOutboxUrl.ShouldBe("/remote.php/dav/calendars/alice/outbox/");
        alice.ScheduleInboxUrl.ShouldBe("/remote.php/dav/calendars/alice/inbox/");
        alice.EmailAddresses.ShouldBe(["alice@example.com"]);
        alice.CalendarUserType.ShouldBe("INDIVIDUAL");
        alice.IsRoomOrResource.ShouldBeFalse();
        Count("REPORT").ShouldBe(0);

        var rootBody = XDocument.Parse(_dav.Server.LogEntries[0].RequestMessage!.Body!);
        rootBody.Descendants(DavNames.CurrentUserPrincipal).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Discovers_another_user_by_calendar_user_address()
    {
        ServeSelf();
        Serve("REPORT", "/remote.php/dav/principals/", "principal-search-bob.xml");

        var result = await Discovery().DiscoverAsync(new MailboxRef("bob@example.com"), Ct);

        var bob = result.Value;
        bob.IsCurrentUser.ShouldBeFalse();
        bob.Href.ShouldBe("/remote.php/dav/principals/users/bob/");
        bob.CalendarHomeSet.ShouldBe("/remote.php/dav/calendars/bob/");
        bob.ScheduleOutboxUrl.ShouldBe("/remote.php/dav/calendars/bob/outbox/");
        bob.AddressBookHomeSet.ShouldBeNull();

        var search = XDocument.Parse(_dav.Server.LogEntries.Single(e => e.RequestMessage!.Method == "REPORT").RequestMessage!.Body!);
        search.Root!.Name.ShouldBe(DavNs.Dav + "principal-property-search");
        search.Root.Attribute("test")!.Value.ShouldBe("anyof");
        search.Descendants(DavNs.Dav + "match").Select(m => m.Value).ShouldBe(["mailto:bob@example.com", "bob@example.com"]);
        search.Descendants(DavNs.Dav + "apply-to-principal-collection-set").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Discovers_a_room_principal()
    {
        ServeSelf();
        Serve("REPORT", "/remote.php/dav/principals/", "principal-search-room.xml");

        var room = (await Discovery().DiscoverAsync(new MailboxRef("boardroom@rooms.example.com"), Ct)).Value;

        room.IsRoomOrResource.ShouldBeTrue();
        room.DisplayName.ShouldBe("Boardroom");
        Count("PROPFIND").ShouldBe(2); // no follow-up PROPFIND for rooms
    }

    [Fact]
    public async Task Follows_up_with_propfind_when_the_search_omits_home_sets()
    {
        ServeSelf();
        Serve("REPORT", "/remote.php/dav/principals/", "principal-search-carol-nohome.xml");
        Serve("PROPFIND", "/remote.php/dav/principals/users/carol/", "propfind-principal-carol.xml");

        var carol = (await Discovery().DiscoverAsync(new MailboxRef("carol@example.com"), Ct)).Value;

        carol.CalendarHomeSet.ShouldBe("/remote.php/dav/calendars/carol/");
        carol.DisplayName.ShouldBe("Carol");
    }

    [Fact]
    public async Task Unknown_mailbox_is_not_found()
    {
        ServeSelf();
        _dav.Server.Given(Request.Create().WithPath("/remote.php/dav/principals/").UsingMethod("REPORT"))
            .RespondWith(Response.Create().WithStatusCode(207).WithBody("<d:multistatus xmlns:d=\"DAV:\"/>"));

        var result = await Discovery().DiscoverAsync(new MailboxRef("nobody@example.com"), Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Search_hits_without_the_address_do_not_match()
    {
        ServeSelf();
        Serve("REPORT", "/remote.php/dav/principals/", "principal-search-bob.xml");
        (await Discovery().DiscoverAsync(new MailboxRef("bo@example.com"), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Theory]
    [InlineData(CredentialStrategy.PassThrough, ErrorKind.BackendAuthFailed)]
    [InlineData(CredentialStrategy.TokenExchange, ErrorKind.BackendUnavailable)] // ADR 0001 §42: façade misconfiguration
    public async Task Unauthenticated_principal_is_what_a_401_is_for_the_credential(CredentialStrategy strategy, ErrorKind kind)
    {
        UseCredential(strategy);
        Serve("PROPFIND", "/remote.php/dav/", "propfind-root-unauthenticated.xml");
        (await Discovery().DiscoverAsync(new MailboxRef("alice@example.com"), Ct)).Error!.Kind.ShouldBe(kind);
    }

    [Theory]
    [InlineData(CredentialStrategy.PassThrough, ErrorKind.BackendAuthFailed)]
    [InlineData(CredentialStrategy.TokenExchange, ErrorKind.BackendUnavailable)]
    public async Task Http_failures_propagate(CredentialStrategy strategy, ErrorKind kind)
    {
        UseCredential(strategy);
        _dav.Server.Given(Request.Create().WithPath("/remote.php/dav/").UsingMethod("PROPFIND")).RespondWith(Response.Create().WithStatusCode(401));
        (await Discovery().DiscoverAsync(new MailboxRef("alice@example.com"), Ct)).Error!.Kind.ShouldBe(kind);
    }

    private void UseCredential(CredentialStrategy strategy) =>
        _dav.Credentials.ForAsync(Backend.Groupware, Arg.Any<CancellationToken>())
            .Returns(Result<BackendCredential>.Success(new BackendCredential("Bearer", DavTestServer.Token, strategy)));

    [Fact]
    public async Task Results_are_cached_per_credential_identity()
    {
        ServeSelf();
        Serve("REPORT", "/remote.php/dav/principals/", "principal-search-bob.xml");
        var discovery = Discovery();

        await discovery.DiscoverAsync(new MailboxRef("alice@example.com"), Ct);
        await discovery.DiscoverAsync(new MailboxRef("bob@example.com"), Ct);
        await discovery.DiscoverAsync(new MailboxRef("BOB@example.com"), Ct);
        await discovery.DiscoverAsync(new MailboxRef("alice@example.com"), Ct);
        (await discovery.CurrentUserAsync(Ct)).Value.IsCurrentUser.ShouldBeTrue();

        Count("PROPFIND").ShouldBe(2);
        Count("REPORT").ShouldBe(1);

        // A different credential is a different identity: nothing is reused.
        _dav.Credentials.ForAsync(Backend.Groupware, Arg.Any<CancellationToken>())
            .Returns(Result<BackendCredential>.Success(new BackendCredential("Bearer", "token-other", CredentialStrategy.TokenExchange)));
        await Discovery().DiscoverAsync(new MailboxRef("alice@example.com"), Ct);
        Count("PROPFIND").ShouldBe(4);
    }

    [Fact]
    public async Task Failures_are_not_cached()
    {
        ServeSelf();
        _dav.Server.Given(Request.Create().WithPath("/remote.php/dav/principals/").UsingMethod("REPORT"))
            .RespondWith(Response.Create().WithStatusCode(207).WithBody("<d:multistatus xmlns:d=\"DAV:\"/>"));
        var discovery = Discovery();
        await discovery.DiscoverAsync(new MailboxRef("nobody@example.com"), Ct);
        await discovery.DiscoverAsync(new MailboxRef("nobody@example.com"), Ct);
        Count("REPORT").ShouldBe(2);
    }

    [Fact]
    public async Task Impersonating_credentials_are_unsupported()
    {
        _dav.Credentials.ForAsync(Backend.Groupware, Arg.Any<CancellationToken>())
            .Returns(Result<BackendCredential>.Success(new BackendCredential("Basic", "svc:pw", CredentialStrategy.ServiceAccount, "bob@example.com")));
        (await Discovery().DiscoverAsync(new MailboxRef("bob@example.com"), Ct)).Error!.Kind.ShouldBe(ErrorKind.Unsupported);
        _dav.Server.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Empty_mailbox_is_a_validation_error() =>
        (await Discovery().DiscoverAsync(new MailboxRef(" "), Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);

    [Fact]
    public void Identity_hash_never_contains_the_token()
    {
        var identity = DavPrincipalDiscovery.IdentityOf(new BackendCredential("Bearer", "secret-token", CredentialStrategy.PassThrough));
        identity.ShouldNotContain("secret");
        identity.Length.ShouldBe(32);
        DavPrincipalDiscovery.IdentityOf(new BackendCredential("Bearer", "other", CredentialStrategy.PassThrough)).ShouldNotBe(identity);
    }

    public void Dispose()
    {
        _cache.Dispose();
        _dav.Dispose();
    }
}

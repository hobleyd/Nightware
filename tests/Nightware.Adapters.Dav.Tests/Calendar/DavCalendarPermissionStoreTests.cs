using Nightware.Adapters.Dav.Calendar;
using Nightware.Adapters.Dav.Core;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;
using static Nightware.Adapters.Dav.Tests.Calendar.CalendarAdapterHarness;

namespace Nightware.Adapters.Dav.Tests.Calendar;

public sealed class DavCalendarPermissionStoreTests : IDisposable
{
    private readonly CalendarAdapterHarness _h = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _h.Dispose();

    private DavCalendarPermissionStore Store() =>
        new(new DavCalendarStore(_h.Scopes, _h.Reader, _h.Writer, _h.Resources, _h.Dav.Client, _h.Dav.Paths), _h.Discovery, _h.Dav.Client, _h.Dav.Paths);

    private CalendarId Personal => new(DavItemId.Resource(_h.Dav.Paths.ToRelative(AlicePersonal)!).ToString());

    [Fact]
    public async Task Sharees_are_read_from_the_invite_with_their_address_and_access()
    {
        _h.Serve("PROPFIND", AlicePersonal, 207, """
            <?xml version="1.0"?><d:multistatus xmlns:d="DAV:" xmlns:oc="http://owncloud.org/ns"><d:response><d:href>/x</d:href><d:propstat><d:prop>
              <oc:invite><oc:user><d:href>principal:principals/users/bob</d:href><oc:common-name>Bob</oc:common-name><oc:access><oc:read-write/></oc:access></oc:user></oc:invite>
            </d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>
            """);
        _h.Serve("PROPFIND", "/remote.php/dav/principals/users/bob/", 207, """
            <?xml version="1.0"?><d:multistatus xmlns:d="DAV:" xmlns:c="urn:ietf:params:xml:ns:caldav"><d:response><d:href>/x</d:href><d:propstat><d:prop>
              <c:calendar-user-address-set><d:href>mailto:bob@nightware.localhost</d:href></c:calendar-user-address-set>
            </d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>
            """);

        var share = (await Store().ListAsync(AliceMailbox, Personal, Ct)).Value.ShouldHaveSingleItem();

        share.EmailAddress.ShouldBe(new EmailAddress(Bob, "Bob"));
        share.Role.ShouldBe(CalendarRole.Write);
    }

    [Fact]
    public async Task Granting_posts_an_oc_share_naming_the_sharees_principal()
    {
        _h.Serve("POST", AlicePersonal, 200);

        var share = (await Store().GrantAsync(AliceMailbox, Personal, Bob, CalendarRole.Read, Ct)).Value;

        share.Role.ShouldBe(CalendarRole.Read);
        var body = BodyOf(_h.Requests("POST").ShouldHaveSingleItem());
        body.ShouldContain("principal:principals/users/bob");
        body.ShouldNotContain("read-write");
    }

    [Fact]
    public async Task Only_a_user_of_this_server_can_be_shared_with()
    {
        _h.Discovery.DiscoverAsync(new MailboxRef("x@nowhere.invalid"), Arg.Any<CancellationToken>()).Returns(Error.NotFound("no"));

        (await Store().GrantAsync(AliceMailbox, Personal, "x@nowhere.invalid", CalendarRole.Read, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }
}

using System.Text;
using System.Text.Json.Nodes;

using Nightware.Adapters.Dav.Administration;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Tests.Administration;

/// <summary><see cref="ISharedCalendars"/> against a WireMock Nextcloud (shapes from the phase 17 step 0 probe).</summary>
public sealed class NextcloudSharedCalendarsTests : IDisposable
{
    private const string Users = "/ocs/v1.php/cloud/users";
    private const string User = "/ocs/v1.php/cloud/users/mbx-desk@acme.example";
    private const string Personal = "/remote.php/dav/calendars/mbx-desk@acme.example/personal/";

    private static readonly MailAddress Desk = MailAddress.TryParse("desk@acme.example", out var desk) ? desk! : throw new InvalidOperationException();

    private readonly NextcloudAdminServer _nextcloud = new();

    public void Dispose() => _nextcloud.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ISharedCalendars Calendars => _nextcloud.Get<ISharedCalendars>();

    private IReadOnlyList<WireMock.RequestMessage> Sent(string method, string path) =>
        [.. _nextcloud.Requests.Select(static e => (WireMock.RequestMessage)e.RequestMessage!).Where(r => r.Method == method && r.AbsolutePath == path)];

    private void Calendar(params string[] resources) =>
        _nextcloud.AnswerRaw("PROPFIND", Personal, 207,
            $"""<?xml version="1.0"?><d:multistatus xmlns:d="DAV:"><d:response><d:href>/cloud{Personal}</d:href></d:response>{string.Concat(resources.Select(r => $"<d:response><d:href>/cloud{Personal}{r}</d:href></d:response>"))}</d:multistatus>""",
            "application/xml; charset=utf-8");

    private static string? BasicUser(WireMock.RequestMessage request) =>
        request.Headers!["Authorization"].Single() is var header && header.StartsWith("Basic ", StringComparison.Ordinal)
            ? Encoding.UTF8.GetString(Convert.FromBase64String(header[6..])).Split(':')[0]
            : null;

    [Fact]
    public void Is_registered_as_the_port() => Calendars.ShouldBeOfType<NextcloudSharedCalendars>();

    [Fact]
    public async Task A_new_mailbox_gets_a_user_a_calendar_without_the_sample_event_and_a_share_with_its_group()
    {
        _nextcloud.Answer("GET", User, 200, 998, message: "User does not exist");
        _nextcloud.Answer("POST", Users, 200, 100, """{"id":"mbx-desk@acme.example"}""");
        Calendar("sample.ics");
        _nextcloud.AnswerRaw("DELETE", Personal + "sample.ics", 204, "");
        _nextcloud.AnswerRaw("POST", Personal, 200, "");

        (await Calendars.EnsureAsync(Desk, "Help desk", "mbx-desk@acme.example", Ct)).IsSuccess.ShouldBeTrue();

        var created = JsonNode.Parse(Sent("POST", Users).Single().Body!)!.AsObject();
        (created["userid"]!.GetValue<string>(), created["email"]!.GetValue<string>(), created["displayName"]!.GetValue<string>())
            .ShouldBe(("mbx-desk@acme.example", "desk@acme.example", "Help desk"));
        created["password"]!.GetValue<string>().Length.ShouldBeGreaterThan(20);
        BasicUser(Sent("PROPFIND", Personal).Single()).ShouldBe("mbx-desk@acme.example", "the calendar is touched as its owner");
        Sent("DELETE", Personal + "sample.ics").ShouldHaveSingleItem();
        Sent("POST", Personal).Single().Body!.ShouldContain("principal:principals/groups/mbx-desk%40acme.example</d:href><o:read-write/>");
    }

    [Fact]
    public async Task An_existing_mailbox_gets_a_fresh_password_and_its_events_are_left_alone()
    {
        _nextcloud.Answer("GET", User, 200, 100, """{"id":"mbx-desk@acme.example","displayname":"Help desk"}""");
        _nextcloud.Answer("PUT", User, 200, 100);
        Calendar("rota.ics");
        _nextcloud.AnswerRaw("POST", Personal, 200, "");

        (await Calendars.EnsureAsync(Desk, "Help desk", "mbx-desk@acme.example", Ct)).IsSuccess.ShouldBeTrue();

        Sent("PUT", User).Select(r => JsonNode.Parse(r.Body!)!["key"]!.GetValue<string>()).ShouldBe(["password", "email", "displayname"]);
        Sent("DELETE", Personal + "rota.ics").ShouldBeEmpty();
        Sent("POST", Personal).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_calendar_nextcloud_will_not_list_is_an_error_and_nothing_is_shared()
    {
        _nextcloud.Answer("GET", User, 200, 100, """{"id":"mbx-desk@acme.example"}""");
        _nextcloud.Answer("PUT", User, 200, 100);
        _nextcloud.AnswerRaw("PROPFIND", Personal, 401, "");

        (await Calendars.EnsureAsync(Desk, "Help desk", "mbx-desk@acme.example", Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        Sent("POST", Personal).ShouldBeEmpty();
    }

    [Fact]
    public async Task Find_and_delete_address_the_mailbox_user()
    {
        _nextcloud.Answer("GET", User, 200, 100, """{"id":"mbx-desk@acme.example","displayname":"Help desk"}""");
        _nextcloud.Answer("DELETE", User, 200, 100);

        (await Calendars.FindAsync(Desk, Ct)).Value.ShouldBe(new SharedCalendarAccount("mbx-desk@acme.example", "Help desk"));
        (await Calendars.DeleteAsync(Desk, Ct)).IsSuccess.ShouldBeTrue();
        Sent("DELETE", User).ShouldHaveSingleItem();
    }
}

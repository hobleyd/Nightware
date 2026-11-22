using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nightware.Adapters.Dav.Calendar.Talk;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Nightware.Adapters.Dav.Tests.Calendar.Talk;

/// <summary>
/// Talk rooms against a WireMock Nextcloud. The replies are Talk 25.0.8's (Nextcloud 35), recorded from the dev
/// stack: a created room is OCS 201 with the room in <c>data</c>; a missing room is HTTP and OCS 404.
/// </summary>
public sealed class NextcloudTalkMeetingsTests : IDisposable
{
    private const string Rooms = "/ocs/v2.php/apps/spreed/api/v4/room";
    private const string Public = "https://cloud.example.com/cloud/";
    private static readonly DateTimeOffset Start = new(2026, 12, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly DavTestServer _dav = new();
    private readonly NextcloudTalkMeetings _talk;

    public NextcloudTalkMeetingsTests() =>
        _talk = new NextcloudTalkMeetings(_dav.Client, Options.Create(new TalkOptions { PublicBaseUrl = new Uri(Public) }), NullLogger<NextcloudTalkMeetings>.Instance);

    public void Dispose() => _dav.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string LastBody => _dav.Server.LogEntries[^1].RequestMessage!.Body!;

    private void Reply(string method, string path, int status, int ocsStatus, string data = "[]") =>
        _dav.Server.Given(Request.Create().WithPath(path).UsingMethod(method)).RespondWith(Response.Create().WithStatusCode(status)
            .WithHeader("Content-Type", "application/json; charset=utf-8")
            .WithBody($$$"""{"ocs":{"meta":{"status":"{{{(ocsStatus < 300 ? "ok" : "failure")}}}","statuscode":{{{ocsStatus}}},"message":""},"data":{{{data}}}}}"""));

    [Fact]
    public async Task A_single_meeting_gets_a_public_room_bound_to_its_span()
    {
        Reply("POST", Rooms, 201, 201, """{"token":"pv8f59mw","type":3,"name":"Q3 review","objectType":"event","objectId":"1796115600#1796119200"}""");

        var url = await _talk.OpenAsync(new OnlineMeetingRequest("  Q3 review ", Start, Start.AddHours(1)), Ct);

        url.Value.ShouldBe(Public + "call/pv8f59mw");
        LastBody.ShouldBe("roomType=3&roomName=Q3+review&objectType=event&objectId=1796115600%231796119200");
    }

    [Fact]
    public async Task A_series_room_is_unbound_and_an_untitled_or_long_subject_still_names_it()
    {
        Reply("POST", Rooms, 201, 201, """{"token":"abcd1234"}""");

        (await _talk.OpenAsync(new OnlineMeetingRequest(null, Start, null), Ct)).Value.ShouldEndWith("/call/abcd1234");
        LastBody.ShouldBe("roomType=3&roomName=Online+meeting");

        await _talk.OpenAsync(new OnlineMeetingRequest(new string('x', 300), Start, null), Ct);
        LastBody.ShouldBe("roomType=3&roomName=" + new string('x', 255));
    }

    [Fact]
    public async Task A_zero_length_or_inverted_span_is_bound_to_its_start()
    {
        Reply("POST", Rooms, 201, 201, """{"token":"abcd1234"}""");

        await _talk.OpenAsync(new OnlineMeetingRequest("x", Start, Start.AddHours(-1)), Ct);

        LastBody.ShouldEndWith("objectId=1796115600%231796115600");
    }

    [Theory]
    [InlineData(403, 403, "[]", ErrorKind.Forbidden)]
    [InlineData(201, 201, """{"type":3}""", ErrorKind.BackendUnavailable)]
    [InlineData(201, 201, """{"token":"NOT A TOKEN"}""", ErrorKind.BackendUnavailable)]
    [InlineData(201, 201, "[]", ErrorKind.BackendUnavailable)]
    public async Task A_room_that_cannot_be_opened_is_an_error(int status, int ocsStatus, string data, ErrorKind kind)
    {
        Reply("POST", Rooms, status, ocsStatus, data);

        (await _talk.OpenAsync(new OnlineMeetingRequest("x", Start, null), Ct)).Error!.Kind.ShouldBe(kind);
    }

    [Fact]
    public async Task Closing_deletes_the_room_and_a_room_already_gone_is_fine()
    {
        Reply("DELETE", Rooms + "/pv8f59mw", 200, 200);
        Reply("DELETE", Rooms + "/gone1234", 404, 404);
        Reply("DELETE", Rooms + "/deny1234", 403, 403);

        (await _talk.CloseAsync(Public + "call/pv8f59mw", Ct)).IsSuccess.ShouldBeTrue();
        (await _talk.CloseAsync(Public + "call/gone1234/", Ct)).IsSuccess.ShouldBeTrue();
        (await _talk.CloseAsync(Public + "call/deny1234", Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task Keeping_unbinds_the_room_and_an_unbound_or_missing_room_is_fine()
    {
        Reply("DELETE", Rooms + "/pv8f59mw/object", 200, 200, """{"token":"pv8f59mw","objectType":""}""");
        Reply("DELETE", Rooms + "/abcd1234/object", 400, 400, """{"error":"object-type"}""");
        Reply("DELETE", Rooms + "/gone1234/object", 404, 404);
        Reply("DELETE", Rooms + "/down1234/object", 503, 503);

        (await _talk.KeepAsync(Public + "call/pv8f59mw", Ct)).IsSuccess.ShouldBeTrue();
        (await _talk.KeepAsync(Public + "call/abcd1234", Ct)).IsSuccess.ShouldBeTrue();
        (await _talk.KeepAsync(Public + "call/gone1234", Ct)).IsSuccess.ShouldBeTrue();
        (await _talk.KeepAsync(Public + "call/down1234", Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Theory]
    [InlineData("https://teams.microsoft.com/l/meetup-join/19%3a")]
    [InlineData("https://cloud.example.com/cloud/call/../index.php")]
    [InlineData("https://cloud.example.com/cloud/call/")]
    [InlineData("https://other.example.com/cloud/call/abcd1234")]
    public async Task A_join_url_this_server_did_not_issue_is_left_alone(string url)
    {
        _talk.TokenOf(url).ShouldBeNull();
        (await _talk.CloseAsync(url, Ct)).IsSuccess.ShouldBeTrue();
        (await _talk.KeepAsync(url, Ct)).IsSuccess.ShouldBeTrue();
        _dav.Server.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public void The_public_url_may_omit_its_trailing_slash()
    {
        var talk = new NextcloudTalkMeetings(_dav.Client, Options.Create(new TalkOptions { PublicBaseUrl = new Uri("https://cloud.example.com/cloud") }), NullLogger<NextcloudTalkMeetings>.Instance);

        talk.TokenOf("https://cloud.example.com/cloud/call/abcd1234").ShouldBe("abcd1234");
        talk.IsEnabled.ShouldBeTrue();
    }
}

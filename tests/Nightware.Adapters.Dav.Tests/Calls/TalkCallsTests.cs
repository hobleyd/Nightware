using System.Text.Json.Nodes;

using Nightware.Adapters.Dav.Calls;
using Nightware.Adapters.Dav.Tests.Talk;
using Nightware.Application.Calls;
using Nightware.Domain.Calls;
using Nightware.Domain.Kernel;

using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Nightware.Adapters.Dav.Tests.Calls;

/// <summary>
/// Delegated calls over Talk (ADR 0009 D7) against a WireMock Nextcloud. The join's <c>Set-Cookie</c> headers are the
/// dev stack's (phase 26 Step 0): the instance session cookie, its passphrase, and the long-lived login cookies the
/// adapter must not keep.
/// </summary>
public sealed class TalkCallsTests : IDisposable
{
    private const string Rooms = TalkAdapterHarness.Rooms;
    private const string Call = TalkAdapterHarness.Calls + "nge9dkks";
    private const string Room = Rooms + "/nge9dkks";
    private const string Cookie = "oc7tfh0d0328=109f52cb; oc_sessionPassphrase=n6RK5F%2FF";
    private const long Started = 1795426358;

    private static readonly string[] SetCookies =
    [
        "oc7tfh0d0328=109f52cb; path=/cloud; secure; HttpOnly; SameSite=Lax",
        "oc_sessionPassphrase=n6RK5F%2FF; path=/cloud; secure; HttpOnly; SameSite=Lax",
        "nc_sameSiteCookielax=true; path=/cloud; httponly;secure; expires=Fri, 31-Dec-2100 23:59:59 GMT; SameSite=lax",
        "nc_session_id=deleted; expires=Thu, 01 Jan 1970 00:00:01 GMT; Max-Age=0; path=/cloud; secure; HttpOnly",
        "nc_username=alice; Path=/cloud; Max-Age=1296000; Secure; HttpOnly; SameSite=Lax",
        "nc_token=dV4VDvHbnztQA1q1; Path=/cloud; Max-Age=1296000; Secure; HttpOnly; SameSite=Lax",
        "nc_session_id=109f52cb; Path=/cloud; Max-Age=1296000; Secure; HttpOnly; SameSite=Lax",
    ];

    private readonly TalkAdapterHarness _talk = new();

    public void Dispose() => _talk.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ICallService Calls => _talk.Get<ICallService>();

    private void ServeJoin(string sessionId = "sess-facade")
    {
        _talk.Server.Given(Request.Create().WithPath(Room + "/participants/active").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                .WithHeader("Set-Cookie", SetCookies)
                .WithBody($$$$"""{"ocs":{"meta":{"status":"ok","statuscode":200,"message":"OK"},"data":{"token":"nge9dkks","sessionId":"{{{{sessionId}}}}","participantType":1}}}"""));
    }

    private void ServePlacing()
    {
        _talk.Ocs("POST", Rooms, 200, """{"token":"nge9dkks","type":1,"name":"bob"}""");
        ServeJoin();
        _talk.Ocs("POST", Call, 200, "null");
        _talk.Ocs("GET", Room + "/participants", 200, """[{"actorType":"users","actorId":"alice","attendeeId":15},{"actorType":"users","actorId":"bob","attendeeId":16}]""");
        _talk.Ocs("POST", Call + "/ring/16", 200, "null");
        ServeRoom(hasCall: true, Started);
    }

    private void ServeRoom(bool hasCall, long start) =>
        _talk.Ocs("GET", Room, 200, $$"""{"token":"nge9dkks","type":1,"hasCall":{{(hasCall ? "true" : "false")}},"callStartTime":{{start}},"participantType":1}""");

    private async Task<string> PlaceAsync()
    {
        ServePlacing();
        var placed = await Calls.PlaceAsync(new NewCall(["bob-id"], ["audio"], "https://bot.example.com/callback"), Ct);
        _talk.Server.ResetLogEntries();
        return placed.Value.Id;
    }

    private string? CookieOf(string method, string path) =>
        _talk.Requests(method, path).Single().RequestMessage!.Headers!.TryGetValue("Cookie", out var cookie) ? cookie.Single() : null;

    [Fact]
    public async Task A_call_to_one_person_rings_them_from_the_one_to_one_room()
    {
        ServePlacing();

        var call = (await Calls.PlaceAsync(new NewCall(["bob-id"], ["audio"], "https://bot.example.com/callback"), Ct)).Value;

        call.State.ShouldBe(CallState.Establishing);
        call.ChatThreadId.ShouldBe("nge9dkks");
        call.JoinWebUrl.ShouldBe(TalkAdapterHarness.Public + "call/nge9dkks");
        call.Source.ShouldBe(new CallParty("alice-id", "Alice Anderson"));
        call.Targets.ShouldBe([new CallParty("bob-id", "Bob Brown")]);
        call.RequestedModalities.ShouldBe(["audio"]);
        call.CallbackUri.ShouldBe("https://bot.example.com/callback");

        _talk.Requests("POST", Rooms).Single().RequestMessage!.Body.ShouldBe("roomType=1&invite=bob");
        _talk.Requests("POST", Call).Single().RequestMessage!.Body.ShouldBe("flags=1&silent=true");

        // Only the session's own two cookies travel; Nextcloud's login cookies never do.
        CookieOf("POST", Call).ShouldBe(Cookie);
        CookieOf("POST", Call + "/ring/16").ShouldBe(Cookie);
        CookieOf("POST", Room + "/participants/active").ShouldBeNull();
        call.Id.ShouldNotContain("109f52cb");
    }

    [Fact]
    public async Task A_call_to_several_people_opens_a_group_room_named_after_them()
    {
        _talk.Names.AccountNameOfAsync("carol-id", Arg.Any<CancellationToken>()).Returns("carol");
        _talk.Ocs("POST", Rooms, 201, """{"token":"nge9dkks","type":2}""");
        ServeJoin();
        _talk.Ocs("POST", Call, 200, "null");
        _talk.Ocs("GET", Room + "/participants", 200,
            """[{"actorType":"users","actorId":"alice","attendeeId":15},{"actorType":"users","actorId":"bob","attendeeId":16},{"actorType":"users","actorId":"carol","attendeeId":17}]""");
        _talk.Ocs("POST", Call + "/ring/16", 200, "null");
        _talk.Ocs("POST", Call + "/ring/17", 200, "null");
        ServeRoom(hasCall: true, Started);

        var call = (await Calls.PlaceAsync(new NewCall(["bob-id", "carol-id"], ["audio", "video"], null), Ct)).Value;

        call.Targets.ShouldBe([new CallParty("bob-id", "Bob Brown"), new CallParty(null, "carol")]);
        JsonNode.Parse(_talk.Requests("POST", Rooms).Single().RequestMessage!.Body!)!.ToJsonString()
            .ShouldBe("""{"roomType":2,"roomName":"Bob Brown, carol","participants":{"users":["bob","carol"]}}""");
        _talk.Requests("POST", Call + "/ring/17").Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_group_room_made_for_a_call_that_fails_is_deleted_again()
    {
        _talk.Names.AccountNameOfAsync("carol-id", Arg.Any<CancellationToken>()).Returns("carol");
        _talk.Ocs("POST", Rooms, 201, """{"token":"nge9dkks","type":2}""");
        ServeJoin();
        _talk.Ocs("POST", Call, 403, """{"error":"permissions"}""");
        _talk.Ocs("DELETE", Call, 200, "null");
        _talk.Ocs("DELETE", Room + "/participants/active", 200, "null");
        _talk.Ocs("DELETE", Room, 200, "null");

        var result = await Calls.PlaceAsync(new NewCall(["bob-id", "carol-id"], ["audio"], null) { Subject = "Stand-up" }, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        _talk.Requests("DELETE", Room).Count.ShouldBe(1);
        _talk.Requests("DELETE", Room + "/participants/active").Count.ShouldBe(1);
        JsonNode.Parse(_talk.Requests("POST", Rooms).Single().RequestMessage!.Body!)!["roomName"]!.GetValue<string>().ShouldBe("Stand-up");
    }

    [Fact]
    public async Task A_target_outside_the_directory_is_a_bad_request()
    {
        (await Calls.PlaceAsync(new NewCall(["nobody"], ["audio"], null), Ct)).Error!.Target.ShouldBe("targets");
        _talk.Server.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_join_without_a_session_cookie_fails_cleanly()
    {
        _talk.Ocs("POST", Rooms, 200, """{"token":"nge9dkks","type":1}""");
        _talk.Ocs("POST", Room + "/participants/active", 200, """{"sessionId":"s"}""");

        (await Calls.PlaceAsync(new NewCall(["bob-id"], ["audio"], null), Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        _talk.Requests("POST", Call).ShouldBeEmpty();
    }

    [Fact]
    public async Task Reading_a_call_never_lists_participants_and_follows_the_rooms_call()
    {
        var id = await PlaceAsync();
        _talk.Ocs("GET", Call, 200, """[{"actorType":"users","actorId":"alice","sessionId":"sess-facade"}]""");

        (await Calls.GetAsync(id, Ct)).Value.State.ShouldBe(CallState.Establishing);

        _talk.Server.Reset();
        ServeRoom(hasCall: true, Started);
        _talk.Ocs("GET", Call, 200, """[{"actorType":"users","actorId":"bob","sessionId":"sess-bob"}]""");
        (await Calls.GetAsync(id, Ct)).Value.State.ShouldBe(CallState.Established);

        _talk.Server.Reset();
        ServeRoom(hasCall: true, Started + 60); // another call since
        (await Calls.GetAsync(id, Ct)).Value.State.ShouldBe(CallState.Terminated);

        _talk.Server.Reset();
        ServeRoom(hasCall: false, 0);
        var ended = (await Calls.GetAsync(id, Ct)).Value;
        ended.State.ShouldBe(CallState.Terminated);
        ended.Targets.ShouldBe([new CallParty("bob-id", "Bob Brown")]);
        ended.CallbackUri.ShouldBe("https://bot.example.com/callback");

        _talk.Server.Reset();
        _talk.Ocs("GET", Room, 404, "null");
        (await Calls.GetAsync(id, Ct)).Value.State.ShouldBe(CallState.Terminated);

        _talk.Requests("GET", Room + "/participants").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_call_id_opens_for_its_caller_only()
    {
        var id = await PlaceAsync();
        using var bob = new TalkAdapterHarness(callerId: "bob-id");

        (await bob.Get<ICallService>().GetAsync(id, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await bob.Get<ICallService>().HangUpAsync(id, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await Calls.GetAsync(id[..^2] + (id[^2] == 'A' ? "B" : "A") + id[^1], Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await Calls.GetAsync("garbage!", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        bob.Server.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Hanging_up_leaves_the_call_for_everyone_and_the_room()
    {
        var id = await PlaceAsync();
        _talk.Ocs("DELETE", Call, 200, "null");
        _talk.Ocs("DELETE", Room + "/participants/active", 200, "null");

        (await Calls.HangUpAsync(id, Ct)).IsSuccess.ShouldBeTrue();

        _talk.Requests("DELETE", Call).Single().RequestMessage!.Query!["all"].Single().ShouldBe("true");
        CookieOf("DELETE", Call).ShouldBe(Cookie);
        CookieOf("DELETE", Room + "/participants/active").ShouldBe(Cookie);
    }

    [Fact]
    public async Task A_reaped_session_still_ends_a_call_that_is_ringing()
    {
        var id = await PlaceAsync();
        _talk.Ocs("DELETE", Call, 404, "null", r => r.WithHeader("Cookie", Cookie));
        ServeRoom(hasCall: true, Started);
        _talk.Ocs("GET", Call, 200, "[]");
        ServeJoin("sess-fresh");
        _talk.Ocs("DELETE", Room + "/participants/active", 200, "null");

        (await Calls.HangUpAsync(id, Ct)).IsSuccess.ShouldBeTrue();

        _talk.Requests("POST", Room + "/participants/active").Count.ShouldBe(1);
        _talk.Requests("DELETE", Call).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Hanging_up_an_ended_call_succeeds_and_an_outage_is_reported()
    {
        var id = await PlaceAsync();
        _talk.Ocs("DELETE", Call, 404, "null");
        ServeRoom(hasCall: false, 0);

        (await Calls.HangUpAsync(id, Ct)).IsSuccess.ShouldBeTrue();
        _talk.Requests("POST").ShouldBeEmpty();

        _talk.Server.Reset();
        _talk.Ocs("DELETE", Call, 503, "null");
        (await Calls.HangUpAsync(id, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public void The_session_cookie_pair_is_found_whatever_the_instance_calls_it()
    {
        TalkCalls.SessionCookie(SetCookies).ShouldBe(Cookie);
        TalkCalls.SessionCookie(["oc_sessionPassphrase=p; path=/", "ocabc=s; path=/"]).ShouldBe("ocabc=s; oc_sessionPassphrase=p");
        TalkCalls.SessionCookie(["ocabc=s; path=/"]).ShouldBeNull();
        TalkCalls.SessionCookie(["oc_sessionPassphrase=p", "nc_token=t", "nc_session_id=s"]).ShouldBeNull();
    }
}

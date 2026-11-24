using System.Net;
using System.Text.Json.Nodes;
using Nightware.Adapters.Jmap.Core;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Core.Requests;
using Nightware.Adapters.Jmap.Tests.Support;
using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;
using Polly;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Nightware.Adapters.Jmap.Tests.Core;

public sealed class JmapClientTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static JmapRequestBuilder MailboxGet(out JmapCall<GetResponse<Mailbox>> call)
    {
        var request = new JmapRequestBuilder();
        call = request.Add(JmapMethods.MailboxGet, new GetRequest("c") { Properties = ["id", "name", "role"] });
        return request;
    }

    [Fact]
    public async Task Resolves_accounts_through_the_session()
    {
        await using var host = new JmapTestHost();
        host.StubSession();
        var client = host.Client();

        (await client.ResolveAccountAsync(new MailboxRef(JmapTestHost.AliceAddress), Ct)).Value.AccountId.ShouldBe("c");
        (await client.ResolveAccountAsync(new MailboxRef("bob@nightware.localhost"), Ct)).Value.AccountId.ShouldBe("d");
        (await client.ResolveAccountAsync(new MailboxRef("nobody@nightware.localhost"), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await client.ResolveAccountAsync(new MailboxRef("bob@nightware.localhost"), Ct, JmapCapabilities.VacationResponse)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task A_mailbox_granted_since_the_session_was_fetched_is_found_by_fetching_it_again()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var host = new JmapTestHost(time: time);
        var withoutBob = JsonNode.Parse(Fixtures.AliceSession)!.AsObject();
        withoutBob["accounts"]!.AsObject().Remove("d");
        host.Server.Given(Request.Create().WithPath("/jmap/session").UsingGet()).InScenario("grant").WillSetStateTo("granted")
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(withoutBob.ToJsonString()));
        host.Server.Given(Request.Create().WithPath("/jmap/session").UsingGet()).InScenario("grant").WhenStateIs("granted")
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(Fixtures.AliceSession));

        // Cached a minute ago, before bob's mailbox was shared: the lookup fetches the session again and finds it.
        (await host.Client().ResolveAccountAsync(new MailboxRef(JmapTestHost.AliceAddress), Ct)).IsSuccess.ShouldBeTrue();
        time.Advance(TimeSpan.FromMinutes(1));
        (await host.Client().ResolveAccountAsync(new MailboxRef("bob@nightware.localhost"), Ct)).Value.AccountId.ShouldBe("d");

        host.Requests("/jmap/session", "GET").Count.ShouldBe(2);
    }

    [Fact]
    public async Task An_address_that_is_no_account_refetches_the_session_at_most_every_thirty_seconds()
    {
        await using var host = new JmapTestHost();
        host.StubSession();

        (await host.Client().ResolveAccountAsync(new MailboxRef("nobody@nightware.localhost"), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await host.Client().ResolveAccountAsync(new MailboxRef("nobody@nightware.localhost"), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await host.Client().ResolveAccountAsync(new MailboxRef("nobody-else@nightware.localhost"), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);

        // The first fetch is fresh, so no lookup refetches it within MinRefreshInterval.
        host.Requests("/jmap/session", "GET").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Sends_a_mailbox_get_and_reads_the_typed_response()
    {
        await using var host = new JmapTestHost();
        host.StubSession();
        host.StubApi("""
            [["Mailbox/get",{"accountId":"c","state":"m1","list":[
              {"id":"a","name":"Inbox","role":"inbox","parentId":null},
              {"id":"b","name":"Deleted Items","role":"trash","parentId":null}],"notFound":[]},"c0"]]
            """);

        var response = await host.Client().SendAsync(MailboxGet(out var get), Ct);

        var mailboxes = response.Bind(r => r.Get(get)).Value;
        mailboxes.State.ShouldBe("m1");
        mailboxes.List.Select(m => m.Role).ShouldBe(["inbox", "trash"]);

        var body = host.ApiBodies().ShouldHaveSingleItem();
        body["using"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe([JmapCapabilities.Core, JmapCapabilities.Mail]);
        body["methodCalls"]!.ToJsonString().ShouldBe("""[["Mailbox/get",{"accountId":"c","properties":["id","name","role"]},"c0"]]""");
        body.ContainsKey("createdIds").ShouldBeFalse();
        JmapTestHost.Authorization(host.Requests("/jmap/", "POST")[0]).ShouldBe("Bearer alice-token");
    }

    [Fact]
    public async Task Back_references_and_capabilities_are_put_on_the_wire()
    {
        await using var host = new JmapTestHost();
        host.StubSession();
        host.StubApi("[]");
        var request = new JmapRequestBuilder();
        var query = request.Add(JmapMethods.EmailQuery, new EmailQueryRequest("c")
        {
            Filter = new EmailFilter { InMailbox = "a" },
            Sort = [new Comparator("receivedAt", IsAscending: false)],
            Limit = 50,
        });
        var emails = request.Add(JmapMethods.EmailGet, new EmailGetRequest("c") { Properties = ["id", "threadId"] }, query.Ref("ids", JmapPaths.Ids));
        request.Add(JmapMethods.ThreadGet, new GetRequest("c"), emails.Ref("ids", JmapPaths.ListThreadIds));
        request.Add(JmapMethods.IdentityGet, new GetRequest("c"));

        await host.Client().SendAsync(request, Ct);

        var body = host.ApiBodies().ShouldHaveSingleItem();
        body["using"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe([JmapCapabilities.Core, JmapCapabilities.Mail, JmapCapabilities.Submission]);
        var calls = body["methodCalls"]!.AsArray();
        calls.Count.ShouldBe(4);
        calls[1]!.ToJsonString().ShouldBe("""["Email/get",{"accountId":"c","properties":["id","threadId"],"#ids":{"resultOf":"c0","name":"Email/query","path":"/ids"}},"c1"]""");
        calls[2]![1]!["#ids"]!.ToJsonString().ShouldBe("""{"resultOf":"c1","name":"Email/get","path":"/list/*/threadId"}""");
        calls[2]![1]!.AsObject().ContainsKey("ids").ShouldBeFalse();
    }

    [Fact]
    public async Task Large_get_id_lists_are_chunked_and_merged()
    {
        await using var host = new JmapTestHost();
        host.StubSession(JmapTestHost.Session(maxObjectsInGet: 2));
        host.StubApi("""
            [["Email/get",{"accountId":"c","state":"s","list":[{"id":"e1"},{"id":"e2"}],"notFound":[]},"c0"],
             ["Email/get",{"accountId":"c","state":"s","list":[{"id":"e3"}],"notFound":["e4"]},"c0.1"],
             ["Email/get",{"accountId":"c","state":"s","list":[{"id":"e5"}],"notFound":[]},"c0.2"]]
            """);
        var request = new JmapRequestBuilder();
        var get = request.Add(JmapMethods.EmailGet, new EmailGetRequest("c") { Ids = ["e1", "e2", "e3", "e4", "e5"], Properties = ["id"] });

        var emails = (await host.Client().SendAsync(request, Ct)).Bind(r => r.Get(get)).Value;

        emails.List.Select(e => e.Id).ShouldBe(["e1", "e2", "e3", "e5"]);
        emails.NotFound.ShouldBe(["e4"]);
        var calls = host.ApiBodies().ShouldHaveSingleItem()["methodCalls"]!.AsArray();
        calls.Select(c => c![2]!.GetValue<string>()).ShouldBe(["c0", "c0.1", "c0.2"]);
        calls.Select(c => c![1]!["ids"]!.AsArray().Count).ShouldBe([2, 2, 1]);
        calls[1]![1]!["properties"]!.ToJsonString().ShouldBe("""["id"]""");
    }

    [Fact]
    public async Task An_error_in_any_chunk_fails_the_call()
    {
        await using var host = new JmapTestHost();
        host.StubSession(JmapTestHost.Session(maxObjectsInGet: 1));
        host.StubApi("""
            [["Email/get",{"accountId":"c","state":"s","list":[{"id":"e1"}],"notFound":[]},"c0"],
             ["error",{"type":"serverUnavailable"},"c0.1"]]
            """);
        var request = new JmapRequestBuilder();
        var get = request.Add(JmapMethods.EmailGet, new EmailGetRequest("c") { Ids = ["e1", "e2"] });

        (await host.Client().SendAsync(request, Ct)).Value.Get(get).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task Requests_beyond_max_calls_are_split_and_created_ids_carried_over()
    {
        await using var host = new JmapTestHost();
        host.StubSession(JmapTestHost.Session(maxCallsInRequest: 2));
        host.Server.Given(Request.Create().WithPath("/jmap/").UsingPost().WithBody(new WildcardMatcher("*\"c0\"*")))
            .RespondWith(JmapTestHost.ApiResponse("""
                [["Mailbox/set",{"accountId":"c","newState":"2","created":{"k1":{"id":"M7"}}},"c0"],
                 ["Mailbox/get",{"accountId":"c","state":"2","list":[],"notFound":[]},"c1"]]
                """, createdIds: """{"k1":"M7"}"""));
        host.Server.Given(Request.Create().WithPath("/jmap/").UsingPost().WithBody(new WildcardMatcher("*\"c2\"*")))
            .RespondWith(JmapTestHost.ApiResponse("""[["Mailbox/get",{"accountId":"c","state":"2","list":[{"id":"M7"}],"notFound":[]},"c2"]]"""));
        var request = new JmapRequestBuilder();
        var create = request.Add(JmapMethods.MailboxSet, new MailboxSetRequest("c") { Create = new Dictionary<string, Mailbox> { ["k1"] = new() { Name = "Projects" } } });
        request.Add(JmapMethods.MailboxGet, new GetRequest("c") { Ids = [] });
        var last = request.Add(JmapMethods.MailboxGet, new GetRequest("c") { Ids = ["#k1"] });

        var response = (await host.Client().SendAsync(request, Ct)).Value;

        response.Get(create).Value.Created!["k1"].Id.ShouldBe("M7");
        response.Get(last).Value.List.ShouldHaveSingleItem().Id.ShouldBe("M7");
        response.CreatedIds["k1"].ShouldBe("M7");
        var bodies = host.ApiBodies();
        bodies.Count.ShouldBe(2);
        bodies[0]["methodCalls"]!.AsArray().Count.ShouldBe(2);
        bodies[1]["createdIds"]!.ToJsonString().ShouldBe("""{"k1":"M7"}""");
    }

    [Fact]
    public async Task A_request_that_must_not_be_split_fails_before_anything_is_sent()
    {
        await using var host = new JmapTestHost();
        host.StubSession(JmapTestHost.Session(maxCallsInRequest: 1));
        host.StubApi("[]");
        var request = new JmapRequestBuilder().RequireSingleRequest();
        request.Add(JmapMethods.MailboxGet, new GetRequest("c"));
        request.Add(JmapMethods.IdentityGet, new GetRequest("c"));

        var result = await host.Client().SendAsync(request, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        host.Requests("/jmap/", "POST").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_request_that_must_not_be_split_is_sent_when_it_fits()
    {
        await using var host = new JmapTestHost();
        host.StubSession(JmapTestHost.Session(maxCallsInRequest: 2));
        host.StubApi("""
            [["Mailbox/get",{"accountId":"c","state":"m1","list":[],"notFound":[]},"c0"],
             ["Identity/get",{"accountId":"c","state":"i1","list":[],"notFound":[]},"c1"]]
            """);
        var request = new JmapRequestBuilder().RequireSingleRequest();
        request.Add(JmapMethods.MailboxGet, new GetRequest("c"));
        request.Add(JmapMethods.IdentityGet, new GetRequest("c"));

        (await host.Client().SendAsync(request, Ct)).IsSuccess.ShouldBeTrue();
        host.Requests("/jmap/", "POST").ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Method_errors_are_mapped_per_call()
    {
        await using var host = new JmapTestHost();
        host.StubSession();
        host.StubApi("""
            [["error",{"type":"cannotCalculateChanges","description":"state too old"},"c0"],
             ["Mailbox/get",{"accountId":"c","state":"m1","list":[],"notFound":[]},"c1"]]
            """);
        var request = new JmapRequestBuilder();
        var changes = request.Add(JmapMethods.EmailChanges, new ChangesRequest("c", "old"));
        var mailboxes = request.Add(JmapMethods.MailboxGet, new GetRequest("c"));

        var response = (await host.Client().SendAsync(request, Ct)).Value;

        var gone = response.Get(changes).Error!;
        gone.Kind.ShouldBe(ErrorKind.SyncStateGone);
        gone.Message.ShouldContain("state too old");
        response.Get(mailboxes).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData("accountNotFound", ErrorKind.NotFound)]
    [InlineData("forbidden", ErrorKind.Forbidden)]
    [InlineData("accountReadOnly", ErrorKind.Forbidden)]
    [InlineData("rateLimit", ErrorKind.Throttled)]
    [InlineData("serverFail", ErrorKind.Unknown)]
    public async Task Method_error_types_map_through_the_client(string type, ErrorKind expected)
    {
        await using var host = new JmapTestHost();
        host.StubSession();
        host.StubApi($$"""[["error",{"type":"{{type}}"},"c0"]]""");

        var response = await host.Client().SendAsync(MailboxGet(out var get), Ct);

        response.Value.Get(get).Error!.Kind.ShouldBe(expected);
    }

    [Fact]
    public async Task Missing_or_unreadable_method_responses_are_unexpected()
    {
        await using var host = new JmapTestHost();
        host.StubSession();
        host.StubApi("""[["Mailbox/get",{"list":"not-a-list"},"c0"]]""");
        var request = MailboxGet(out var get);
        var other = request.Add(JmapMethods.IdentityGet, new GetRequest("c"));

        var response = (await host.Client().SendAsync(request, Ct)).Value;

        response.Get(get).Error!.Kind.ShouldBe(ErrorKind.Unknown);
        response.Get(other).Error!.Message.ShouldContain("no response");
    }

    [Fact]
    public async Task Implicit_responses_are_readable()
    {
        await using var host = new JmapTestHost();
        host.StubSession();
        host.StubApi("""
            [["EmailSubmission/set",{"accountId":"c","newState":"1","created":{"s":{"id":"S1"}}},"c0"],
             ["Email/set",{"accountId":"c","newState":"9","updated":{"M1":null}},"c0"]]
            """);
        var request = new JmapRequestBuilder();
        var submit = request.Add(JmapMethods.EmailSubmissionSet, new EmailSubmissionSetRequest("c")
        {
            Create = new Dictionary<string, EmailSubmission> { ["s"] = new() { IdentityId = "i1", EmailId = "M1" } },
        });

        var response = (await host.Client().SendAsync(request, Ct)).Value;

        response.Get(submit).Value.Created!["s"].Id.ShouldBe("S1");
        response.GetAdditional(submit, JmapMethods.EmailSet).Value.Updated!.ShouldContainKey("M1");
        response.GetAdditional(submit, JmapMethods.MailboxSet).Error!.Kind.ShouldBe(ErrorKind.Unknown);
    }

    [Fact]
    public async Task A_changed_session_state_refreshes_the_session()
    {
        await using var host = new JmapTestHost();
        host.StubSession();
        host.StubApi("""[["Mailbox/get",{"accountId":"c","state":"m","list":[],"notFound":[]},"c0"]]""", sessionState: "changed");

        await host.Client().SendAsync(MailboxGet(out _), Ct);
        await host.Client().SendAsync(MailboxGet(out _), Ct);

        host.Requests("/jmap/session", "GET").Count.ShouldBe(2);
    }

    [Fact]
    public async Task An_unchanged_session_state_keeps_the_cached_session()
    {
        await using var host = new JmapTestHost();
        host.StubSession();
        host.StubApi("""[["Mailbox/get",{"accountId":"c","state":"m","list":[],"notFound":[]},"c0"]]""");

        await host.Client().SendAsync(MailboxGet(out _), Ct);
        var response = await host.Client().SendAsync(MailboxGet(out _), Ct);

        response.Value.SessionState.ShouldBe("3e25b2a0");
        host.Requests("/jmap/session", "GET").Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(CredentialStrategy.PassThrough, ErrorKind.BackendAuthFailed)]
    [InlineData(CredentialStrategy.TokenExchange, ErrorKind.BackendUnavailable)]
    [InlineData(CredentialStrategy.ServiceAccount, ErrorKind.BackendUnavailable)]
    public async Task Api_401_drops_the_session_and_re_authenticates_only_a_passed_through_token(CredentialStrategy strategy, ErrorKind expected)
    {
        await using var host = new JmapTestHost();
        host.UseCredential(new BackendCredential("Bearer", "token", strategy));
        host.StubSession();
        host.Server.Given(Request.Create().WithPath("/jmap/").UsingPost()).RespondWith(Response.Create().WithStatusCode(401));

        (await host.Client().SendAsync(MailboxGet(out _), Ct)).Error!.Kind.ShouldBe(expected);
        await host.Client().GetSessionAsync(Ct);

        host.Requests("/jmap/session", "GET").Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("""{"type":"urn:ietf:params:jmap:error:limit","limit":"maxSizeRequest","status":400}""", ErrorKind.PayloadTooLarge)]
    [InlineData("""{"type":"urn:ietf:params:jmap:error:limit","limit":"maxConcurrentRequests","status":400}""", ErrorKind.Throttled)]
    [InlineData("""{"type":"urn:ietf:params:jmap:error:unknownCapability","status":400,"detail":"urn:x"}""", ErrorKind.Unsupported)]
    [InlineData("""{"type":"urn:ietf:params:jmap:error:notJSON","status":400}""", ErrorKind.Unknown)]
    [InlineData("""{"type":"urn:ietf:params:jmap:error:notRequest","status":400}""", ErrorKind.Unknown)]
    public async Task Request_level_problems_are_mapped(string problem, ErrorKind expected)
    {
        await using var host = new JmapTestHost();
        host.StubSession();
        host.Server.Given(Request.Create().WithPath("/jmap/").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400).WithHeader("Content-Type", "application/problem+json").WithBody(problem));

        (await host.Client().SendAsync(MailboxGet(out _), Ct)).Error!.Kind.ShouldBe(expected);
    }

    [Theory]
    [InlineData(429, ErrorKind.Throttled)]
    [InlineData(503, ErrorKind.BackendUnavailable)]
    [InlineData(500, ErrorKind.BackendUnavailable)]
    public async Task Retry_after_is_honoured_in_errors(int status, ErrorKind expected)
    {
        await using var host = new JmapTestHost();
        host.StubSession();
        host.Server.Given(Request.Create().WithPath("/jmap/").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Retry-After", "17"));

        var error = (await host.Client().SendAsync(MailboxGet(out _), Ct)).Error!;

        error.Kind.ShouldBe(expected);
        error.RetryAfterSeconds.ShouldBe(17);
    }

    [Fact]
    public async Task Unreadable_api_responses_are_unexpected()
    {
        await using var host = new JmapTestHost();
        host.StubSession();
        host.Server.Given(Request.Create().WithPath("/jmap/").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("""{"methodResponses":[["x"]]}"""));

        (await host.Client().SendAsync(MailboxGet(out _), Ct)).Error!.Kind.ShouldBe(ErrorKind.Unknown);
    }

    [Fact]
    public async Task Timeouts_are_backend_unavailable()
    {
        await using var host = new JmapTestHost(s =>
        {
            s["Jmap:AttemptTimeout"] = "00:00:00.200";
            s["Jmap:RequestTimeout"] = "00:00:00.500";
        });
        host.StubSession();
        host.Server.Given(Request.Create().WithPath("/jmap/").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(3)).WithBody("{}"));

        (await host.Client().SendAsync(MailboxGet(out _), Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task Unreachable_servers_are_backend_unavailable()
    {
        await using var host = new JmapTestHost(s => s["Jmap:SessionUrl"] = "http://127.0.0.1:1/jmap/session");

        (await host.Client().GetSessionAsync(Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        await using var host = new JmapTestHost();
        host.Server.Given(Request.Create().WithPath("/jmap/session").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithDelay(TimeSpan.FromSeconds(3)).WithBody(Fixtures.AliceSession));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Should.ThrowAsync<OperationCanceledException>(() => host.Client().GetSessionAsync(cts.Token));
    }

    [Fact]
    public async Task Safe_requests_are_retried_on_transient_failures()
    {
        await using var host = new JmapTestHost(s => s["Jmap:MaxRetryAttempts"] = "2");
        host.StubSession();
        host.Server.Given(Request.Create().WithPath("/jmap/").UsingPost())
            .InScenario("flaky").WillSetStateTo("ok")
            .RespondWith(Response.Create().WithStatusCode(503));
        host.Server.Given(Request.Create().WithPath("/jmap/").UsingPost())
            .InScenario("flaky").WhenStateIs("ok")
            .RespondWith(JmapTestHost.ApiResponse("""[["Mailbox/get",{"accountId":"c","state":"m","list":[],"notFound":[]},"c0"]]"""));

        var response = await host.Client().SendAsync(MailboxGet(out var get), Ct);

        response.Value.Get(get).IsSuccess.ShouldBeTrue();
        host.Requests("/jmap/", "POST").Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_throttled_request_is_surfaced_not_replayed()
    {
        await using var host = new JmapTestHost(s => s["Jmap:MaxRetryAttempts"] = "2");
        host.StubSession();
        host.Server.Given(Request.Create().WithPath("/jmap/").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(429).WithHeader("Retry-After", "30"));

        var error = (await host.Client().SendAsync(MailboxGet(out _), Ct)).Error!;

        (error.Kind, error.RetryAfterSeconds).ShouldBe((ErrorKind.Throttled, 30));
        host.Requests("/jmap/", "POST").Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_failing_server_opens_the_circuit_so_later_calls_fail_at_once()
    {
        await using var host = new JmapTestHost(s => s["Jmap:MaxRetryAttempts"] = "0");
        host.StubSession();
        host.Server.Given(Request.Create().WithPath("/jmap/").UsingPost()).RespondWith(Response.Create().WithStatusCode(503));

        for (var i = 0; i < 20; i++)
        {
            await host.Client().SendAsync(MailboxGet(out _), Ct);
        }

        var sent = host.Requests("/jmap/", "POST").Count;
        (await host.Client().SendAsync(MailboxGet(out _), Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        host.Requests("/jmap/", "POST").Count.ShouldBe(sent, "the open circuit answers without a request");
    }

    [Theory]
    [InlineData(500, true)]
    [InlineData(502, true)]
    [InlineData(503, true)]
    [InlineData(504, true)]
    [InlineData(429, false)]
    [InlineData(400, false)]
    [InlineData(404, false)]
    [InlineData(507, false)]
    public void Only_server_wide_failures_count_towards_the_circuit(int status, bool counts) =>
        JmapAdapterServiceCollectionExtensions.IsServerFailure(Outcome.FromResult(new HttpResponseMessage((HttpStatusCode)status))).ShouldBe(counts);

    [Fact]
    public void Network_errors_and_attempt_timeouts_count_towards_the_circuit()
    {
        JmapAdapterServiceCollectionExtensions.IsServerFailure(Outcome.FromException<HttpResponseMessage>(new HttpRequestException())).ShouldBeTrue();
        JmapAdapterServiceCollectionExtensions.IsServerFailure(Outcome.FromException<HttpResponseMessage>(new Polly.Timeout.TimeoutRejectedException())).ShouldBeTrue();
        JmapAdapterServiceCollectionExtensions.IsServerFailure(Outcome.FromException<HttpResponseMessage>(new OperationCanceledException())).ShouldBeFalse();
    }

    [Fact]
    public async Task Requests_with_set_calls_are_never_retried()
    {
        await using var host = new JmapTestHost(s => s["Jmap:MaxRetryAttempts"] = "2");
        host.StubSession();
        host.Server.Given(Request.Create().WithPath("/jmap/").UsingPost()).RespondWith(Response.Create().WithStatusCode(503));
        var request = MailboxGet(out _);
        request.Add(JmapMethods.MailboxSet, new MailboxSetRequest("c") { Destroy = ["x"] });

        (await host.Client().SendAsync(request, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);

        host.Requests("/jmap/", "POST").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Empty_requests_are_bugs()
    {
        await using var host = new JmapTestHost();

        await Should.ThrowAsync<ArgumentException>(() => host.Client().SendAsync(new JmapRequestBuilder(), Ct));
    }

    [Fact]
    public async Task Response_session_state_is_exposed()
    {
        await using var host = new JmapTestHost();
        host.StubSession();
        host.StubApi("""[["Mailbox/get",{"accountId":"c","state":"m","list":[],"notFound":[]},"c0"]]""", sessionState: "3e25b2a0");

        var response = (await host.Client().SendAsync(MailboxGet(out _), Ct)).Value;

        response.SessionState.ShouldBe("3e25b2a0");
        response.MethodResponses.ShouldHaveSingleItem().Name.ShouldBe("Mailbox/get");
        JsonNode.Parse(response.MethodResponses[0].Arguments.GetRawText())!["state"]!.GetValue<string>().ShouldBe("m");
    }
}

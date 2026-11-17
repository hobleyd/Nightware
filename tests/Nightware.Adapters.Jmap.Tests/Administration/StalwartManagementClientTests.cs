using System.Text;

using Nightware.Adapters.Jmap.Administration;
using Nightware.Adapters.Jmap.Core.Requests;
using Nightware.Adapters.Jmap.Tests.Support;
using Nightware.Domain.Kernel;

using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Nightware.Adapters.Jmap.Tests.Administration;

public sealed class StalwartManagementClientTests : IAsyncDisposable
{
    private StalwartManagementTestHost _host = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (JmapRequestBuilder Request, JmapCall<Nightware.Adapters.Jmap.Core.Protocol.QueryResponse> Query) AccountQuery()
    {
        var request = new JmapRequestBuilder();
        var query = request.Add(StalwartMethods.AccountQuery, new StalwartQueryRequest<AccountFilter> { Limit = 1 });
        return (request, query);
    }

    private static JmapRequestBuilder DomainCreate()
    {
        var request = new JmapRequestBuilder();
        request.Add(StalwartMethods.DomainSet, new StalwartSetRequest { Destroy = ["x"] });
        return request;
    }

    private Task<Result<JmapResponse>> SendAsync(JmapRequestBuilder request) => _host.Get<IStalwartManagementClient>().SendAsync(request, Ct);

    [Fact]
    public async Task Posts_to_the_api_url_with_the_stalwart_capability_and_no_account_id()
    {
        _host.Stub("x:Account/query", """[["x:Account/query",{"ids":["a"],"position":0,"queryState":"q"},"c0"]]""");
        var (request, query) = AccountQuery();

        var response = await SendAsync(request);

        response.Value.Get(query).Value.Ids.ShouldBe(["a"]);
        var body = _host.Bodies().Single();
        body["using"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["urn:ietf:params:jmap:core", "urn:stalwart:jmap"]);
        var call = body["methodCalls"]![0]!.AsArray();
        call[0]!.GetValue<string>().ShouldBe("x:Account/query");
        call[1]!.AsObject().ContainsKey("accountId").ShouldBeFalse();
        call[1]!["limit"]!.GetValue<int>().ShouldBe(1);
        call[2]!.GetValue<string>().ShouldBe("c0");
    }

    [Fact]
    public async Task Authenticates_with_basic_for_a_password()
    {
        _host.Stub("x:Account/query", """[["x:Account/query",{"ids":[]},"c0"]]""");

        await SendAsync(AccountQuery().Request);

        var expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{StalwartManagementTestHost.Username}:{StalwartManagementTestHost.Password}"));
        Authorization().ShouldBe(expected);
    }

    [Fact]
    public async Task Authenticates_with_bearer_for_an_api_key()
    {
        await _host.DisposeAsync();
        _host = new StalwartManagementTestHost(s =>
        {
            s["StalwartManagement:Password"] = null;
            s["StalwartManagement:ApiKey"] = StalwartManagementTestHost.ApiKey;
        });
        _host.Stub("x:Account/query", """[["x:Account/query",{"ids":[]},"c0"]]""");

        await SendAsync(AccountQuery().Request);

        Authorization().ShouldBe("Bearer " + StalwartManagementTestHost.ApiKey);
    }

    [Fact]
    public async Task A_rejected_credential_is_backend_unavailable_and_never_logged()
    {
        _host.StubStatus(401, """{"type":"about:blank","status":401,"title":"Unauthorized","detail":"You have to authenticate first."}""", "application/problem+json");

        var response = await SendAsync(AccountQuery().Request);

        response.Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        response.Error.Message.ShouldContain("management credential");
        _host.Logs.AllText.ShouldContain("rejected the admin console's management credential");
        _host.Logs.AllText.ShouldNotContain(StalwartManagementTestHost.Password);
        _host.Logs.AllText.ShouldNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes($"{StalwartManagementTestHost.Username}:{StalwartManagementTestHost.Password}")));
    }

    [Fact]
    public async Task The_api_key_never_reaches_the_logs()
    {
        await _host.DisposeAsync();
        _host = new StalwartManagementTestHost(s =>
        {
            s["StalwartManagement:Password"] = null;
            s["StalwartManagement:ApiKey"] = StalwartManagementTestHost.ApiKey;
        });
        _host.StubStatus(500);

        (await SendAsync(AccountQuery().Request)).IsSuccess.ShouldBeFalse();

        _host.Logs.Lines.ShouldNotBeEmpty();
        _host.Logs.AllText.ShouldNotContain(StalwartManagementTestHost.ApiKey);
    }

    [Theory]
    [InlineData(403, ErrorKind.Forbidden)]
    [InlineData(404, ErrorKind.BackendUnavailable)]
    [InlineData(429, ErrorKind.Throttled)]
    [InlineData(500, ErrorKind.BackendUnavailable)]
    [InlineData(503, ErrorKind.BackendUnavailable)]
    [InlineData(400, ErrorKind.Unknown)]
    public async Task Maps_http_failures(int status, ErrorKind kind)
    {
        _host.StubStatus(status);

        (await SendAsync(AccountQuery().Request)).Error!.Kind.ShouldBe(kind);
        _host.Logs.AllText.ShouldContain($"HTTP {status}");
    }

    [Fact]
    public async Task Maps_a_jmap_problem_document()
    {
        _host.StubStatus(400, """{"type":"urn:ietf:params:jmap:error:unknownCapability","status":400,"detail":"urn:stalwart:jmap"}""", "application/problem+json");

        (await SendAsync(AccountQuery().Request)).Error!.Kind.ShouldBe(ErrorKind.Unsupported);
    }

    [Fact]
    public async Task Maps_a_method_error_per_call()
    {
        _host.Stub("x:Account/query", """[["error",{"type":"forbidden","description":"sysAccountQuery"},"c0"]]""");
        var (request, query) = AccountQuery();

        var response = await SendAsync(request);

        response.IsSuccess.ShouldBeTrue();
        response.Value.Get(query).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task An_unreadable_response_is_unexpected()
    {
        _host.StubStatus(200, "<html>proxy</html>", "text/html");

        (await SendAsync(AccountQuery().Request)).Error!.Kind.ShouldBe(ErrorKind.Unknown);
    }

    [Fact]
    public async Task An_unreachable_server_is_backend_unavailable()
    {
        await _host.DisposeAsync();
        _host = new StalwartManagementTestHost(s => s["StalwartManagement:ApiUrl"] = "http://127.0.0.1:1/jmap/");

        var response = await SendAsync(AccountQuery().Request);

        response.Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        _host.Logs.AllText.ShouldContain("unreachable");
    }

    [Fact]
    public async Task Retries_a_read_only_request()
    {
        await _host.DisposeAsync();
        _host = new StalwartManagementTestHost(s => s["StalwartManagement:MaxRetryAttempts"] = "2");
        FlakyOnce("""[["x:Account/query",{"ids":["a"]},"c0"]]""");
        var (request, query) = AccountQuery();

        var response = await SendAsync(request);

        response.Value.Get(query).Value.Ids.ShouldBe(["a"]);
        _host.Bodies().Count.ShouldBe(2);
    }

    [Fact]
    public async Task Never_retries_a_write()
    {
        await _host.DisposeAsync();
        _host = new StalwartManagementTestHost(s => s["StalwartManagement:MaxRetryAttempts"] = "2");
        FlakyOnce("""[["x:Domain/set",{"destroyed":["x"]},"c0"]]""");

        var response = await SendAsync(DomainCreate());

        response.Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        _host.Bodies().Count.ShouldBe(1);
    }

    private void FlakyOnce(string methodResponses)
    {
        _host.Server.Given(Request.Create().WithPath("/jmap/").UsingPost())
            .InScenario("flaky").WillSetStateTo("ok")
            .RespondWith(Response.Create().WithStatusCode(503));
        _host.Server.Given(Request.Create().WithPath("/jmap/").UsingPost())
            .InScenario("flaky").WhenStateIs("ok")
            .RespondWith(StalwartManagementTestHost.Ok(methodResponses));
    }

    private string? Authorization()
    {
        var entry = _host.Server.LogEntries.Single();
        return entry.RequestMessage!.Headers!.TryGetValue("Authorization", out var values) ? values.Single() : null;
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();
}
using System.Net;
using System.Net.Http.Headers;
using Nightware.Adapters.Dav.Core.Http;

namespace Nightware.Adapters.Dav.Tests.Core.Http;

public sealed class NextcloudSessionHandlerTests : IDisposable
{
    private readonly NextcloudSessions _sessions = new(TimeProvider.System);
    private readonly Recorder _backend = new();
    private readonly HttpMessageInvoker _client;

    public NextcloudSessionHandlerTests() =>
        _client = new HttpMessageInvoker(new NextcloudSessionHandler(_sessions) { InnerHandler = _backend });

    public void Dispose()
    {
        _client.Dispose();
        _sessions.Dispose();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<HttpResponseMessage> Send(AuthenticationHeaderValue? authorization, string? cookie = null, bool ownSession = false)
    {
        var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), "http://nextcloud.test/remote.php/dav/calendars/x/");
        request.Headers.Authorization = authorization;
        if (ownSession)
        {
            request.Options.Set(DavRequestOptions.OwnSession, true);
        }

        if (cookie is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        return _client.SendAsync(request, Ct);
    }

    private static AuthenticationHeaderValue Bearer(string sub, long exp = 1) => new("Bearer", NextcloudSessionsTests.Jwt(sub, exp: exp));

    [Fact]
    public async Task A_users_session_goes_back_on_their_later_requests_only()
    {
        _backend.SetCookies = ["ocabc=alice-session; path=/; secure; HttpOnly"];
        await Send(Bearer("alice", exp: 1));
        _backend.SetCookies = [];
        await Send(Bearer("alice", exp: 2)); // a refreshed token for the same user
        await Send(Bearer("bob"));

        _backend.Cookies.ShouldBe([null, "ocabc=alice-session", null]);
        _backend.Requests[1].Headers.GetValues("OCS-APIRequest").ShouldBe(["true"]);
        _backend.Requests[1].Headers.Authorization!.Scheme.ShouldBe("Bearer");
        _backend.Requests[2].Headers.Contains("OCS-APIRequest").ShouldBeFalse();
    }

    [Fact]
    public async Task Unauthorized_forgets_the_session()
    {
        _backend.SetCookies = ["ocabc=s1"];
        await Send(Bearer("alice"));
        _backend.SetCookies = [];
        _backend.Status = HttpStatusCode.Unauthorized;
        await Send(Bearer("alice"));
        _backend.Status = HttpStatusCode.OK;
        await Send(Bearer("alice"));

        _backend.Cookies.ShouldBe([null, "ocabc=s1", null]);
    }

    [Fact]
    public async Task Requests_without_a_bearer_keep_no_session_and_send_no_cookie()
    {
        _backend.SetCookies = ["ocabc=s1"];
        await Send(new AuthenticationHeaderValue("Basic", "YWxpY2U6cHc="), cookie: "stray=1");
        await Send(null);
        await Send(new AuthenticationHeaderValue("Basic", "YWxpY2U6cHc="));

        _backend.Cookies.ShouldBe([null, null, null]);
    }

    [Fact]
    public async Task A_cookie_header_from_elsewhere_is_replaced()
    {
        await Send(Bearer("alice"), cookie: "stray=1");
        _backend.Cookies.ShouldBe([null]);
    }

    [Fact]
    public async Task A_request_in_its_own_session_keeps_its_cookie_and_leaves_the_shared_session_alone()
    {
        _backend.SetCookies = ["ocabc=alice-session"];
        await Send(Bearer("alice"));

        // A Talk call's session: its own cookie goes out as given, and what Nextcloud sets is not kept.
        _backend.SetCookies = ["ocabc=call-session"];
        await Send(Bearer("alice"), cookie: "ocabc=call; oc_sessionPassphrase=p", ownSession: true);
        await Send(Bearer("alice"), ownSession: true);
        _backend.SetCookies = [];
        await Send(Bearer("alice"));

        _backend.Cookies.ShouldBe([null, "ocabc=call; oc_sessionPassphrase=p", null, "ocabc=alice-session"]);
    }

    private sealed class Recorder : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public List<string?> Cookies { get; } = [];

        public string[] SetCookies { get; set; } = [];

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Cookies.Add(request.Headers.TryGetValues("Cookie", out var values) ? string.Join("; ", values) : null);
            var response = NextcloudSessionsTests.Response(SetCookies);
            response.StatusCode = Status;
            return Task.FromResult(response);
        }
    }
}

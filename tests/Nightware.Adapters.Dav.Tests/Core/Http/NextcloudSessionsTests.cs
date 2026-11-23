using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Nightware.Adapters.Dav.Core.Http;

namespace Nightware.Adapters.Dav.Tests.Core.Http;

public sealed class NextcloudSessionsTests : IDisposable
{
    private static readonly Uri Origin = new("http://nextcloud.test/remote.php/dav/");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 23, 0, 0, 0, TimeSpan.Zero));
    private readonly NextcloudSessions _sessions;

    public NextcloudSessionsTests() => _sessions = new NextcloudSessions(_time);

    public void Dispose() => _sessions.Dispose();

    internal static string Jwt(string sub, string iss = "https://kc.test/realms/nw", long exp = 1)
    {
        static string Part(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return Part("{\"alg\":\"RS256\"}") + "." + Part($"{{\"iss\":\"{iss}\",\"sub\":\"{sub}\",\"exp\":{exp}}}") + ".c2ln";
    }

    private static AuthenticationHeaderValue Bearer(string token) => new("Bearer", token);

    [Fact]
    public void Tokens_of_one_user_share_a_key()
    {
        NextcloudSessions.KeyFor(Origin, Bearer(Jwt("alice", exp: 1))).ShouldBe(NextcloudSessions.KeyFor(Origin, Bearer(Jwt("alice", exp: 2))));
    }

    [Fact]
    public void Users_issuers_and_servers_get_distinct_keys()
    {
        var alice = NextcloudSessions.KeyFor(Origin, Bearer(Jwt("alice")));
        NextcloudSessions.KeyFor(Origin, Bearer(Jwt("bob"))).ShouldNotBe(alice);
        NextcloudSessions.KeyFor(Origin, Bearer(Jwt("alice", iss: "https://other.test"))).ShouldNotBe(alice);
        NextcloudSessions.KeyFor(new Uri("http://other.test/"), Bearer(Jwt("alice"))).ShouldNotBe(alice);
    }

    [Fact]
    public void Opaque_tokens_are_keyed_on_the_token_and_never_held_in_clear()
    {
        var key = NextcloudSessions.KeyFor(Origin, Bearer("opaque-token"));
        key.ShouldNotBeNull();
        key.ShouldNotContain("opaque");
        NextcloudSessions.KeyFor(Origin, Bearer("other-token")).ShouldNotBe(key);
    }

    [Theory]
    [InlineData("a.%%%.c")]
    [InlineData("a.W10.c")] // payload is a JSON array
    [InlineData("a.e30.c")] // payload has no sub
    public void Malformed_jwts_fall_back_to_the_token(string token)
    {
        NextcloudSessions.KeyFor(Origin, Bearer(token)).ShouldNotBeNull();
    }

    [Fact]
    public void Only_bearer_credentials_get_a_session()
    {
        NextcloudSessions.KeyFor(Origin, null).ShouldBeNull();
        NextcloudSessions.KeyFor(Origin, new AuthenticationHeaderValue("Basic", "YWxpY2U6cHc=")).ShouldBeNull();
        NextcloudSessions.KeyFor(Origin, new AuthenticationHeaderValue("Bearer")).ShouldBeNull();
    }

    [Fact]
    public void Cookies_set_by_nextcloud_go_back()
    {
        _sessions.Remember("k", Response(
            "ocabc=s1; path=/; secure; HttpOnly; SameSite=Lax",
            "oc_sessionPassphrase=p1; path=/; secure; HttpOnly",
            "__Host-nc_sameSiteCookiestrict=true; path=/; secure; HttpOnly; expires=Fri, 31-Dec-2100 23:59:59 GMT; SameSite=strict"));

        _sessions.CookieHeader("k").ShouldBe("ocabc=s1; oc_sessionPassphrase=p1; __Host-nc_sameSiteCookiestrict=true");
        _sessions.CookieHeader("other").ShouldBeNull();
    }

    [Theory]
    [InlineData("ocabc=deleted; expires=Thu, 01-Jan-1970 00:00:01 GMT")]
    [InlineData("ocabc=x; Max-Age=0")]
    [InlineData("ocabc=x; expires=Sun, 22-Nov-2026 00:00:00 GMT")]
    [InlineData("ocabc=")]
    public void Expired_cookies_are_dropped(string setCookie)
    {
        _sessions.Remember("k", Response("ocabc=s1", "keep=1"));
        _sessions.Remember("k", Response(setCookie));
        _sessions.CookieHeader("k").ShouldBe("keep=1");
    }

    [Fact]
    public void A_new_value_replaces_the_old_and_junk_is_ignored()
    {
        _sessions.Remember("k", Response("ocabc=s1", "=nameless", "novalue"));
        _sessions.Remember("k", Response("ocabc=s2; Max-Age=3600"));
        _sessions.CookieHeader("k").ShouldBe("ocabc=s2");
    }

    [Fact]
    public void Responses_without_cookies_create_nothing()
    {
        _sessions.Remember("k", new HttpResponseMessage());
        _sessions.CookieHeader("k").ShouldBeNull();
    }

    [Fact]
    public void Idle_sessions_expire_and_used_ones_stay()
    {
        _sessions.Remember("idle", Response("a=1"));
        _sessions.Remember("used", Response("b=1"));
        for (var i = 0; i < 4; i++)
        {
            _time.Advance(NextcloudSessions.IdleTimeout / 3);
            _sessions.CookieHeader("used").ShouldBe("b=1");
        }

        _sessions.CookieHeader("idle").ShouldBeNull();
    }

    [Fact]
    public void Forget_drops_the_session()
    {
        _sessions.Remember("k", Response("a=1"));
        _sessions.Forget("k");
        _sessions.CookieHeader("k").ShouldBeNull();
    }

    internal static HttpResponseMessage Response(params string[] setCookies)
    {
        var response = new HttpResponseMessage();
        foreach (var value in setCookies)
        {
            response.Headers.TryAddWithoutValidation("Set-Cookie", value);
        }

        return response;
    }
}

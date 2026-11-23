using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Nightware.Integration.Tests.Directory;
using Nightware.Integration.Tests.Support;

namespace Nightware.Integration.Tests.OnlineMeetings;

/// <summary>
/// Phase 26's live harness: an in-process Nightware with Talk and calls on (<c>Talk:PublicBaseUrl</c>, a throwaway
/// <c>Calls:SealingKey</c>), and a cookie-free backchannel to look at Talk and the files as alice or bob directly.
/// </summary>
internal sealed class TalkLive : IAsyncDisposable
{
    private readonly DevStack _dev;

    private TalkLive()
    {
        Stack = LiveStack.Start(new Dictionary<string, string?>
        {
            ["Talk:PublicBaseUrl"] = DevStack.CloudBase + "/",
            ["Calls:SealingKey"] = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
        });
        _dev = DevStack.Open();

        // Cookie-free: Nextcloud answers a bearer request with a session cookie, and bearer + another user's session is trouble.
        Backchannel = new HttpClient(_dev.Trusting(new SocketsHttpHandler { UseCookies = false }));
    }

    public LiveStack Stack { get; }

    public HttpClient Backchannel { get; }

    public static TalkLive Start() => new();

    /// <summary>An OCS request (below <c>/ocs/v2.php/</c>) as <paramref name="user"/>; returns <c>ocs.data</c> and the HTTP status.</summary>
    public async Task<(int Status, JsonElement Data)> OcsAsync(string user, HttpMethod method, string path, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, $"{DevStack.CloudBase}/ocs/v2.php/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await Stack.NextcloudTokenAsync(user, cancellationToken));
        request.Headers.Add("OCS-APIRequest", "true");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await Backchannel.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return ((int)response.StatusCode, body.GetProperty("ocs").GetProperty("data").Clone());
    }

    /// <summary>
    /// Joins a Talk room as <paramref name="user"/>'s own client would (<c>participants/active</c>) and returns the session's
    /// cookie pair (the instance's session cookie and its passphrase) for later requests in that session.
    /// </summary>
    public async Task<string> JoinRoomAsync(string user, string token, CancellationToken cancellationToken)
    {
        using var response = await SendOcsAsync(user, HttpMethod.Post, $"apps/spreed/api/v4/room/{token}/participants/active", null, null, cancellationToken);
        response.EnsureSuccessStatusCode();
        var cookies = response.Headers.GetValues("Set-Cookie").Select(c => c.Split(';', 2)[0].Split('=', 2)).Where(p => p.Length == 2 && p[1] != "deleted").ToList();
        var sessionId = cookies.Last(c => c[0] == "nc_session_id")[1];
        var session = cookies.Last(c => !c[0].StartsWith("nc_", StringComparison.Ordinal) && c[0] != "oc_sessionPassphrase" && c[1] == sessionId);
        var passphrase = cookies.Last(c => c[0] == "oc_sessionPassphrase");
        return $"{session[0]}={session[1]}; {passphrase[0]}={passphrase[1]}";
    }

    /// <summary>An OCS request in a Talk session (<paramref name="cookie"/> from <see cref="JoinRoomAsync"/>); returns the HTTP status.</summary>
    public async Task<int> InSessionAsync(string user, HttpMethod method, string path, string cookie, IReadOnlyDictionary<string, string>? form, CancellationToken cancellationToken)
    {
        using var response = await SendOcsAsync(user, method, path, cookie, form, cancellationToken);
        return (int)response.StatusCode;
    }

    private async Task<HttpResponseMessage> SendOcsAsync(
        string user, HttpMethod method, string path, string? cookie, IReadOnlyDictionary<string, string>? form, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, $"{DevStack.CloudBase}/ocs/v2.php/{path}") { Content = form is null ? null : new FormUrlEncodedContent(form) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await Stack.NextcloudTokenAsync(user, cancellationToken));
        request.Headers.Add("OCS-APIRequest", "true");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        return await Backchannel.SendAsync(request, cancellationToken);
    }

    /// <summary>A WebDAV request (below <c>/remote.php/dav/</c>) as <paramref name="user"/>.</summary>
    public async Task<HttpResponseMessage> DavAsync(string user, HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, $"{DevStack.CloudBase}/remote.php/dav/{path}") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await Stack.NextcloudTokenAsync(user, cancellationToken));
        return await Backchannel.SendAsync(request, cancellationToken);
    }

    /// <summary>A CalDAV <c>calendar-query</c> REPORT (Depth 1) on a collection below <c>/remote.php/dav/</c> as <paramref name="user"/>; returns the body.</summary>
    public async Task<string> ReportAsync(string user, string path, string query, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(new HttpMethod("REPORT"), $"{DevStack.CloudBase}/remote.php/dav/{path}")
        {
            Content = new StringContent(query, System.Text.Encoding.UTF8, "application/xml"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await Stack.NextcloudTokenAsync(user, cancellationToken));
        request.Headers.Add("Depth", "1");
        using var response = await Backchannel.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    /// <summary>Creates a Talk room directly as <paramref name="user"/> (as an older Nightware did); returns its token.</summary>
    public async Task<string> CreateRoomAsync(string user, HttpContent form, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{DevStack.CloudBase}/ocs/v2.php/apps/spreed/api/v4/room") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await Stack.NextcloudTokenAsync(user, cancellationToken));
        request.Headers.Add("OCS-APIRequest", "true");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await Backchannel.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return body.GetProperty("ocs").GetProperty("data").GetProperty("token").GetString()!;
    }

    public async ValueTask DisposeAsync()
    {
        Backchannel.Dispose();
        _dev.Dispose();
        await Stack.DisposeAsync();
    }
}

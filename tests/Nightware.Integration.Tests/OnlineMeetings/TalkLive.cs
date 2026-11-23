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

    /// <summary>A WebDAV request (below <c>/remote.php/dav/</c>) as <paramref name="user"/>.</summary>
    public async Task<HttpResponseMessage> DavAsync(string user, HttpMethod method, string path, HttpContent? content, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, $"{DevStack.CloudBase}/remote.php/dav/{path}") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await Stack.NextcloudTokenAsync(user, cancellationToken));
        return await Backchannel.SendAsync(request, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        Backchannel.Dispose();
        _dev.Dispose();
        await Stack.DisposeAsync();
    }
}

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;

using Nightware.Integration.Tests.Support;

namespace Nightware.Integration.Tests.Files;

/// <summary>
/// An in-process Nightware against the running dev stack for Phase 7 (Nextcloud links): Nextcloud through
/// its public name, <c>https://cloud.{domain}</c> advertised as the file site, and Collabora reached on its
/// loopback port (<c>NIGHTWARE_COLLABORA_URL</c>, default <c>http://127.0.0.1:9980/</c>). Seeds files and
/// shares as alice directly over WebDAV/OCS, as the Nextcloud web UI would. Opt-in: skips unless
/// <c>NIGHTWARE_INTEGRATION=1</c>.
/// </summary>
internal sealed class FilesLiveHost : IAsyncDisposable
{
    public const string Domain = DevStack.Domain;
    public const string CloudBase = "https://cloud." + Domain;
    public const string Alice = "alice";
    public const string Bob = "bob";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly DevStack _stack;
    private readonly HttpClient _backchannel;
    private readonly Uri _collabora;

    private FilesLiveHost(DevStack stack)
    {
        _stack = stack;
        // Cookie-free: Nextcloud answers a bearer request with a session cookie, and bearer + session is a 401.
        _backchannel = new HttpClient(stack.Trusting(new SocketsHttpHandler { UseCookies = false }));
        _collabora = new Uri(Environment.GetEnvironmentVariable("NIGHTWARE_COLLABORA_URL") ?? "http://127.0.0.1:9980/");
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:Authority"] = DevStack.Issuer,
                ["Discovery:GraphBaseUrl"] = DevStack.GraphBase,
                ["Discovery:GraphBetaBaseUrl"] = DevStack.GraphBetaBase,
                ["Discovery:FilesBaseUrls:0"] = CloudBase,
                ["DeltaLinks:SigningKey"] = "integration",
                ["Directory:KeycloakBaseUrl"] = "https://auth." + Domain,
                ["Directory:ClientSecret"] = stack.Env("NIGHTWARE_API_CLIENT_SECRET"),
                ["Dav:BaseUrl"] = CloudBase + "/",
                ["Collabora:BaseUrl"] = _collabora.AbsoluteUri,
            }));
            builder.ConfigureTestServices(stack.Trust);
        });
    }

    public static FilesLiveHost Start() => new(DevStack.Open());

    /// <summary>Graph's share id for a sharing URL — exactly NightMail's <c>encodeSharingUrl</c>.</summary>
    public static string ShareId(string url) => "u!" + Convert.ToBase64String(Encoding.UTF8.GetBytes(url)).TrimEnd('=').Replace('/', '_').Replace('+', '-');

    /// <summary>The in-process façade as <paramref name="user"/>, not following redirects (as NightMail's Graph client).</summary>
    public async Task<HttpClient> GraphAsync(string user, CancellationToken cancellationToken)
    {
        var client = Anonymous();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await UserTokenAsync(user, cancellationToken));
        return client;
    }

    /// <summary>The façade with no <c>Authorization</c> header — NightMail's pre-authenticated download client.</summary>
    public HttpClient Anonymous() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>The path and query of a URL the façade emitted on its public base.</summary>
    public static string Local(Uri link)
    {
        link.AbsoluteUri.ShouldStartWith(DevStack.GraphBase + "/", Case.Sensitive, "URLs must sit on the configured graphBaseUrl");
        return "/v1.0" + link.AbsoluteUri[DevStack.GraphBase.Length..];
    }

    /// <summary>A document rendered by Collabora from plain text (<c>docx</c>, <c>pdf</c>…), to seed realistic files.</summary>
    public async Task<byte[]> RenderAsync(string text, string format, CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(text)), "data", "seed.txt" } };
        using var response = await _backchannel.PostAsync(new Uri(_collabora, "cool/convert-to/" + format), form, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    /// <summary>Uploads a file into <paramref name="user"/>'s Files at <paramref name="path"/> (folders created).</summary>
    public async Task UploadAsync(string user, string path, byte[] content, CancellationToken cancellationToken)
    {
        var token = await NextcloudTokenAsync(user, cancellationToken);
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var folder = $"{CloudBase}/remote.php/dav/files/{user}";
        foreach (var segment in segments[..^1])
        {
            folder += "/" + Uri.EscapeDataString(segment);
            using var mkcol = new HttpRequestMessage(new HttpMethod("MKCOL"), folder);
            mkcol.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var _ = await _backchannel.SendAsync(mkcol, cancellationToken);
        }

        using var put = new HttpRequestMessage(HttpMethod.Put, folder + "/" + Uri.EscapeDataString(segments[^1])) { Content = new ByteArrayContent(content) };
        put.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _backchannel.SendAsync(put, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Shares <paramref name="path"/> through the OCS share API; returns the share's <c>url</c> (public link) and the file id.</summary>
    public async Task<(string? Url, long FileId)> ShareAsync(string user, string path, int shareType, CancellationToken cancellationToken, string? shareWith = null, string? password = null)
    {
        var form = new List<KeyValuePair<string, string>> { new("path", path), new("shareType", shareType.ToString(System.Globalization.CultureInfo.InvariantCulture)) };
        if (shareWith is not null)
        {
            form.Add(new("shareWith", shareWith));
        }

        if (password is not null)
        {
            form.Add(new("password", password));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, CloudBase + "/ocs/v2.php/apps/files_sharing/api/v1/shares?format=json")
        {
            Content = new FormUrlEncodedContent(form),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await NextcloudTokenAsync(user, cancellationToken));
        request.Headers.Add("OCS-APIRequest", "true");
        using var response = await _backchannel.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        response.IsSuccessStatusCode.ShouldBeTrue(body.ToString());
        var data = body.GetProperty("ocs").GetProperty("data");
        return (data.TryGetProperty("url", out var url) ? url.GetString() : null, data.GetProperty("file_source").GetInt64());
    }

    public async ValueTask DisposeAsync()
    {
        _backchannel.Dispose();
        await _factory.DisposeAsync();
        _stack.Dispose();
    }

    private Task<string> UserTokenAsync(string user, CancellationToken cancellationToken) =>
        TokenAsync(
            [
                new("grant_type", "password"),
                new("client_id", "nightware-dev"),
                new("client_secret", _stack.Env("NIGHTWARE_DEV_CLIENT_SECRET")),
                new("username", user),
                new("password", _stack.Env("TEST_USER_PASSWORD")),
                new("scope", "openid email profile"),
            ],
            null,
            cancellationToken);

    private async Task<string> NextcloudTokenAsync(string user, CancellationToken cancellationToken) =>
        await TokenAsync(
            [
                new("grant_type", "urn:ietf:params:oauth:grant-type:token-exchange"),
                new("subject_token", await UserTokenAsync(user, cancellationToken)),
                new("subject_token_type", "urn:ietf:params:oauth:token-type:access_token"),
                new("requested_token_type", "urn:ietf:params:oauth:token-type:access_token"),
                new("audience", "nextcloud"),
            ],
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("nightware-api:" + _stack.Env("NIGHTWARE_API_CLIENT_SECRET")))),
            cancellationToken);

    private async Task<string> TokenAsync(IEnumerable<KeyValuePair<string, string>> form, AuthenticationHeaderValue? auth, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, DevStack.Issuer + "/protocol/openid-connect/token") { Content = new FormUrlEncodedContent(form) };
        request.Headers.Authorization = auth;
        using var response = await _backchannel.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("access_token").GetString()!;
    }
}

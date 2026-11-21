using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Nightware.Integration.Tests.Support;

namespace Nightware.Integration.Tests.Mail.Read;

/// <summary>
/// An in-process Nightware against the running dev stack, as NightMail sees it (real Keycloak tokens,
/// token exchange to Stalwart), plus a direct JMAP channel to seed and change alice's mail the way
/// another client would. Opt-in: skips unless <c>NIGHTWARE_INTEGRATION=1</c>.
/// </summary>
/// <remarks>
/// The stack's CA and address (<see cref="DevStack"/>) apply to Nightware's own HTTP clients only, never process-wide.
/// </remarks>
internal sealed class MailLiveHost : IAsyncDisposable
{
    public static string GraphBase => DevStack.GraphBase;

    public static string Alice => DevStack.Address("alice");

    private static string Issuer => DevStack.Issuer;

    private static string MailBase => DevStack.MailBase;

    private readonly WebApplicationFactory<Program> _factory;
    private readonly DevStack _stack;
    private readonly HttpClient _backchannel;
    private string? _jmapToken;

    private MailLiveHost(DevStack stack)
    {
        _stack = stack;
        _backchannel = stack.Backchannel();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(stack.FacadeSettings()));
            builder.ConfigureTestServices(stack.Trust);
        });
    }

    public static MailLiveHost Start()
    {
        return new MailLiveHost(DevStack.Open());
    }

    /// <summary>The in-process Graph façade, authenticated as alice with a NightMail-shaped token.</summary>
    public async Task<HttpClient> GraphAsync(CancellationToken cancellationToken)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await UserTokenAsync(cancellationToken));
        return client;
    }

    /// <summary>The path and query of a link the façade emitted on its public base.</summary>
    public static string Local(string link)
    {
        link.ShouldStartWith(GraphBase + "/", Case.Sensitive, "links must sit on the configured graphBaseUrl");
        return "/v1.0" + link[GraphBase.Length..];
    }

    /// <summary>Alice's JMAP account id and her mailboxes by role.</summary>
    public async Task<(string Account, IReadOnlyDictionary<string, string> MailboxByRole)> MailboxesAsync(CancellationToken cancellationToken)
    {
        var session = await GetJsonAsync(MailBase + "/jmap/session", cancellationToken);
        var account = session["primaryAccounts"]!["urn:ietf:params:jmap:mail"]!.GetValue<string>();
        var response = await JmapAsync([new JsonArray("Mailbox/get", new JsonObject { ["accountId"] = account, ["properties"] = new JsonArray("id", "role") }, "m")], cancellationToken);
        var roles = response[0]![1]!["list"]!.AsArray()
            .Where(static m => m!["role"] is not null)
            .ToDictionary(static m => m!["role"]!.GetValue<string>(), static m => m!["id"]!.GetValue<string>());
        return (account, roles);
    }

    /// <summary>Imports raw MIME into a mailbox (as delivery would) and returns the JMAP email id.</summary>
    public async Task<string> ImportAsync(string account, string mailboxId, string mime, CancellationToken cancellationToken, DateTimeOffset? receivedAt = null)
    {
        using var upload = new HttpRequestMessage(HttpMethod.Post, $"{MailBase}/jmap/upload/{account}/") { Content = new StringContent(mime, Encoding.UTF8) };
        upload.Content.Headers.ContentType = new MediaTypeHeaderValue("message/rfc822");
        upload.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await JmapTokenAsync(cancellationToken));
        using var uploaded = await _backchannel.SendAsync(upload, cancellationToken);
        uploaded.EnsureSuccessStatusCode();
        var blobId = (await uploaded.Content.ReadFromJsonAsync<JsonObject>(cancellationToken))!["blobId"]!.GetValue<string>();

        var import = new JsonObject
        {
            ["accountId"] = account,
            ["emails"] = new JsonObject
            {
                ["i"] = new JsonObject { ["blobId"] = blobId, ["mailboxIds"] = new JsonObject { [mailboxId] = true }, ["keywords"] = new JsonObject() },
            },
        };
        if (receivedAt is { } received)
        {
            import["emails"]!["i"]!["receivedAt"] = received.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        }

        var response = await JmapAsync([new JsonArray("Email/import", import, "i")], cancellationToken);
        return response[0]![1]!["created"]!["i"]!["id"]!.GetValue<string>();
    }

    /// <summary>Applies a JMAP <c>Email/set</c> update (e.g. a move or a keyword) or destroys an email.</summary>
    public async Task SetAsync(string account, string emailId, JsonObject? patch, CancellationToken cancellationToken)
    {
        var set = new JsonObject { ["accountId"] = account };
        if (patch is null)
        {
            set["destroy"] = new JsonArray(emailId);
        }
        else
        {
            set["update"] = new JsonObject { [emailId] = patch };
        }

        var response = await JmapAsync([new JsonArray("Email/set", set, "s")], cancellationToken);
        var result = response[0]![1]!.AsObject();
        (result["notUpdated"] ?? result["notDestroyed"]).ShouldBeNull(result.ToJsonString());
    }

    public async ValueTask DisposeAsync()
    {
        _backchannel.Dispose();
        await _factory.DisposeAsync();
        _stack.Dispose();
    }

    private async Task<JsonArray> JmapAsync(JsonArray calls, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["using"] = new JsonArray("urn:ietf:params:jmap:core", "urn:ietf:params:jmap:mail"),
            ["methodCalls"] = calls,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, MailBase + "/jmap/") { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await JmapTokenAsync(cancellationToken));
        using var response = await _backchannel.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken))!["methodResponses"]!.AsArray();
    }

    private async Task<JsonObject> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await JmapTokenAsync(cancellationToken));
        using var response = await _backchannel.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken))!;
    }

    private Task<string> UserTokenAsync(CancellationToken cancellationToken) =>
        TokenAsync(
            [
                new("grant_type", "password"),
                new("client_id", "nightware-dev"),
                new("client_secret", _stack.Env("NIGHTWARE_DEV_CLIENT_SECRET")),
                new("username", "alice"),
                new("password", _stack.Env("TEST_USER_PASSWORD")),
                new("scope", "openid email profile"),
            ],
            null,
            cancellationToken);

    private async Task<string> JmapTokenAsync(CancellationToken cancellationToken) =>
        _jmapToken ??= await TokenAsync(
            [
                new("grant_type", "urn:ietf:params:oauth:grant-type:token-exchange"),
                new("subject_token", await UserTokenAsync(cancellationToken)),
                new("subject_token_type", "urn:ietf:params:oauth:token-type:access_token"),
                new("requested_token_type", "urn:ietf:params:oauth:token-type:access_token"),
                new("audience", "stalwart"),
            ],
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("nightware-api:" + _stack.Env("NIGHTWARE_API_CLIENT_SECRET")))),
            cancellationToken);

    private async Task<string> TokenAsync(IEnumerable<KeyValuePair<string, string>> form, AuthenticationHeaderValue? auth, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Issuer + "/protocol/openid-connect/token") { Content = new FormUrlEncodedContent(form) };
        request.Headers.Authorization = auth;
        using var response = await _backchannel.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("access_token").GetString()!;
    }
}

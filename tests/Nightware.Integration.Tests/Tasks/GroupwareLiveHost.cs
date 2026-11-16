using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;

using Nightware.Integration.Tests.Support;

namespace Nightware.Integration.Tests.Tasks;

/// <summary>
/// An in-process Nightware against the running dev stack with every backend wired (Keycloak, Stalwart,
/// Nextcloud), plus direct JMAP access per user to act as another mail client. Opt-in: skips unless
/// <c>NIGHTWARE_INTEGRATION=1</c>. Used by the Tasks and MailboxSettings live tests.
/// </summary>
/// <remarks>
/// The stack's trust and addressing (<see cref="DevStack"/>) apply to Nightware's own HTTP clients (including
/// those that set their own primary handler) and to this host's backchannel only, never process-wide.
/// </remarks>
internal sealed class GroupwareLiveHost : IAsyncDisposable
{
    public const string Alice = "alice";
    public const string Bob = "bob";

    private readonly WebApplicationFactory<Program> _factory;
    private readonly DevStack _stack;
    private readonly HttpClient _backchannel;

    private GroupwareLiveHost(DevStack stack)
    {
        _stack = stack;
        _backchannel = stack.Backchannel();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(stack.FacadeSettings()));
            // After the app's own client configuration, so it also reaches clients that set their own primary handler.
            builder.ConfigureTestServices(stack.Trust);
        });
    }

    public static string GraphBase => DevStack.GraphBase;

    public static GroupwareLiveHost Start() => new(DevStack.Open());

    public static string Address(string user) => DevStack.Address(user);

    /// <summary>The in-process façade, authenticated as <paramref name="user"/> with a NightMail-shaped token.</summary>
    public async Task<HttpClient> GraphAsync(string user, CancellationToken cancellationToken)
    {
        var client = Anonymous();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await UserTokenAsync(user, cancellationToken));
        return client;
    }

    /// <summary>The in-process façade with no <c>Authorization</c> header — how NightMail PUTs an upload URL.</summary>
    public HttpClient Anonymous() => _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>The path and query of a URL the façade emitted on its public base.</summary>
    public static string Local(string link)
    {
        link.ShouldStartWith(GraphBase + "/", Case.Sensitive, "URLs must sit on the configured graphBaseUrl");
        return "/v1.0" + link[GraphBase.Length..];
    }

    /// <summary>Sends a plain-text message from <paramref name="user"/> over JMAP, as another mail client would.</summary>
    public async Task SendAsync(string user, string to, string subject, string text, CancellationToken cancellationToken)
    {
        var (account, roles) = await MailboxesAsync(user, cancellationToken);
        var identities = await JmapAsync(user, [new JsonArray("Identity/get", new JsonObject { ["accountId"] = account }, "i")], cancellationToken);
        var identity = identities[0]![1]!["list"]!.AsArray().FirstOrDefault()?["id"]?.GetValue<string>();
        if (identity is null)
        {
            var created = await JmapAsync(user, [new JsonArray("Identity/set", new JsonObject
            {
                ["accountId"] = account,
                ["create"] = new JsonObject { ["n"] = new JsonObject { ["email"] = Address(user), ["name"] = user } },
            }, "c")], cancellationToken);
            identity = created[0]![1]!["created"]!["n"]!["id"]!.GetValue<string>();
        }

        var email = new JsonObject
        {
            ["mailboxIds"] = new JsonObject { [roles["sent"]] = true },
            ["keywords"] = new JsonObject { ["$seen"] = true },
            ["from"] = new JsonArray(new JsonObject { ["email"] = Address(user) }),
            ["to"] = new JsonArray(new JsonObject { ["email"] = to }),
            ["subject"] = subject,
            ["bodyValues"] = new JsonObject { ["b"] = new JsonObject { ["value"] = text } },
            ["textBody"] = new JsonArray(new JsonObject { ["partId"] = "b", ["type"] = "text/plain" }),
        };
        var sent = await JmapAsync(user,
            [
                new JsonArray("Email/set", new JsonObject { ["accountId"] = account, ["create"] = new JsonObject { ["e"] = email } }, "e"),
                new JsonArray("EmailSubmission/set", new JsonObject
                {
                    ["accountId"] = account,
                    ["create"] = new JsonObject { ["s"] = new JsonObject { ["emailId"] = "#e", ["identityId"] = identity } },
                }, "s"),
            ],
            cancellationToken);
        sent[1]![1]!["created"]?["s"].ShouldNotBeNull(sent.ToJsonString());
    }

    /// <summary>Waits for a message in <paramref name="user"/>'s inbox whose text contains <paramref name="marker"/>.</summary>
    public async Task<JsonObject?> WaitForMessageAsync(string user, string marker, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var (account, roles) = await MailboxesAsync(user, cancellationToken);
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var found = await JmapAsync(user,
                [
                    new JsonArray("Email/query", new JsonObject { ["accountId"] = account, ["filter"] = new JsonObject { ["inMailbox"] = roles["inbox"], ["text"] = marker } }, "q"),
                    new JsonArray("Email/get", new JsonObject
                    {
                        ["accountId"] = account,
                        ["#ids"] = new JsonObject { ["resultOf"] = "q", ["name"] = "Email/query", ["path"] = "/ids" },
                        ["properties"] = new JsonArray("subject", "from", "preview"),
                    }, "g"),
                ],
                cancellationToken);
            if (found[1]![1]!["list"]!.AsArray().FirstOrDefault() is JsonObject message)
            {
                return message;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        _backchannel.Dispose();
        await _factory.DisposeAsync();
        _stack.Dispose();
    }

    private async Task<(string Account, IReadOnlyDictionary<string, string> Roles)> MailboxesAsync(string user, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, DevStack.MailBase + "/jmap/session");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await JmapTokenAsync(user, cancellationToken));
        using var response = await _backchannel.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var session = (await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken))!;
        var account = session["primaryAccounts"]!["urn:ietf:params:jmap:mail"]!.GetValue<string>();
        var mailboxes = await JmapAsync(user, [new JsonArray("Mailbox/get", new JsonObject { ["accountId"] = account, ["properties"] = new JsonArray("id", "role") }, "m")], cancellationToken);
        var roles = mailboxes[0]![1]!["list"]!.AsArray()
            .Where(static m => m!["role"] is not null)
            .ToDictionary(static m => m!["role"]!.GetValue<string>(), static m => m!["id"]!.GetValue<string>());
        return (account, roles);
    }

    private async Task<JsonArray> JmapAsync(string user, JsonArray calls, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["using"] = new JsonArray("urn:ietf:params:jmap:core", "urn:ietf:params:jmap:mail", "urn:ietf:params:jmap:submission"),
            ["methodCalls"] = calls,
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, DevStack.MailBase + "/jmap/") { Content = JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await JmapTokenAsync(user, cancellationToken));
        using var response = await _backchannel.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken))!["methodResponses"]!.AsArray();
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

    private async Task<string> JmapTokenAsync(string user, CancellationToken cancellationToken) =>
        await TokenAsync(
            [
                new("grant_type", "urn:ietf:params:oauth:grant-type:token-exchange"),
                new("subject_token", await UserTokenAsync(user, cancellationToken)),
                new("subject_token_type", "urn:ietf:params:oauth:token-type:access_token"),
                new("requested_token_type", "urn:ietf:params:oauth:token-type:access_token"),
                new("audience", "stalwart"),
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

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Nightware.Integration.Tests.Support;

namespace Nightware.Integration.Tests.Directory;

/// <summary>
/// The running dev stack (<c>deploy/</c>) and an in-process Nightware pointed at it. Opt-in: tests skip
/// unless <c>NIGHTWARE_INTEGRATION=1</c>.
/// </summary>
/// <remarks>
/// <para>The stack's host, secrets and (with <c>CADDY_TLS=internal</c>) Caddy root CA live in the bootstrapped
/// deploy folder (<c>deploy/.env</c>, <c>deploy/.certs/caddy-root.crt</c>), which is git-ignored. Set
/// <c>NIGHTWARE_DEPLOY_DIR</c> when that is not this checkout's own <c>deploy/</c>.</para>
/// <para>Every backend is reached on its public URL (<c>https://{PUBLIC_HOST}/auth</c>, <c>/cloud</c>, …) through
/// Caddy; the CA is trusted for Nightware's outgoing HTTP clients and JWKS fetch only, never process-wide.</para>
/// </remarks>
internal sealed class LiveStack : IAsyncDisposable
{
    public static string GraphBase => DevStack.GraphBase;

    public static string CloudBase => DevStack.CloudBase;

    public static string Issuer => DevStack.Issuer;

    private readonly WebApplicationFactory<Program> _factory;
    private readonly DevStack _stack;

    private LiveStack(DevStack stack)
    {
        _stack = stack;
        Backchannel = stack.Backchannel();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(stack.FacadeSettings()));
            builder.ConfigureTestServices(stack.Trust);
        });
    }

    public static bool Enabled => DevStack.Enabled;

    /// <summary>An HTTP client trusting the stack's CA, for setting up data directly on the backends.</summary>
    public HttpClient Backchannel { get; }

    public static LiveStack Start()
    {
        return new LiveStack(DevStack.Open());
    }

    /// <summary>A client of the in-process Nightware carrying <paramref name="user"/>'s NightMail-shaped token.</summary>
    public async Task<HttpClient> GraphClientAsync(string user, CancellationToken cancellationToken)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await UserTokenAsync(user, cancellationToken));
        return client;
    }

    /// <summary>A password-grant token from the dev-only <c>nightware-dev</c> client (<c>aud nightware-api</c>).</summary>
    public Task<string> UserTokenAsync(string user, CancellationToken cancellationToken) =>
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

    /// <summary><paramref name="user"/>'s token exchanged for audience <c>nextcloud</c>, as the façade would send it.</summary>
    public Task<string> NextcloudTokenAsync(string user, CancellationToken cancellationToken) => ExchangedTokenAsync(user, "nextcloud", cancellationToken);

    /// <summary><paramref name="user"/>'s token exchanged (RFC 8693) for a backend <paramref name="audience"/>, as the façade does.</summary>
    public async Task<string> ExchangedTokenAsync(string user, string audience, CancellationToken cancellationToken)
    {
        var subject = await UserTokenAsync(user, cancellationToken);
        return await TokenAsync(
            [
                new("grant_type", "urn:ietf:params:oauth:grant-type:token-exchange"),
                new("subject_token", subject),
                new("subject_token_type", "urn:ietf:params:oauth:token-type:access_token"),
                new("requested_token_type", "urn:ietf:params:oauth:token-type:access_token"),
                new("audience", audience),
            ],
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("nightware-api:" + _stack.Env("NIGHTWARE_API_CLIENT_SECRET")))),
            cancellationToken);
    }

    /// <summary>The path and query of a link Nightware emitted on its public base, for the in-process client.</summary>
    public static string Local(string link)
    {
        link.ShouldStartWith(GraphBase + "/", Case.Sensitive, "links must sit on the configured graphBaseUrl");
        return "/v1.0" + link[GraphBase.Length..];
    }

    public async ValueTask DisposeAsync()
    {
        Backchannel.Dispose();
        await _factory.DisposeAsync();
        _stack.Dispose();
    }

    private async Task<string> TokenAsync(IEnumerable<KeyValuePair<string, string>> form, AuthenticationHeaderValue? auth, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Issuer + "/protocol/openid-connect/token") { Content = new FormUrlEncodedContent(form) };
        request.Headers.Authorization = auth;
        using var response = await Backchannel.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        return body.GetProperty("access_token").GetString()!;
    }
}

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Nightware.Integration.Tests.Support;

namespace Nightware.Integration.Tests.Calendar;

/// <summary>
/// The running dev stack and an in-process Nightware pointed at it (the same shape as 5A's and Phase 2's live
/// hosts). Opt-in with <c>NIGHTWARE_INTEGRATION=1</c>; <c>NIGHTWARE_DEPLOY_DIR</c> names a bootstrapped deploy
/// folder (<c>.env</c>, else <c>.env.example</c>, and <c>.certs/</c>). The Caddy CA is trusted only by Nightware's
/// outgoing clients and JWKS fetch, never process-wide.
/// </summary>
internal sealed class CalendarLiveStack : IAsyncDisposable
{
    public const string Domain = DevStack.Domain;
    public const string Issuer = DevStack.Issuer;

    private readonly WebApplicationFactory<Program> _factory;
    private readonly DevStack _stack;
    private readonly HttpClient _backchannel;

    private CalendarLiveStack(DevStack stack)
    {
        _stack = stack;
        _backchannel = stack.Backchannel();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:Authority"] = Issuer,
                ["DeltaLinks:SigningKey"] = "integration",
                ["Directory:ClientSecret"] = stack.Env("NIGHTWARE_API_CLIENT_SECRET"),
                ["Jmap:SessionUrl"] = "https://mail." + Domain + "/.well-known/jmap",
            }));
            builder.ConfigureTestServices(stack.Trust);
        });
    }

    public static CalendarLiveStack Start()
    {
        return new CalendarLiveStack(DevStack.Open());
    }

    /// <summary>A client of the in-process Nightware carrying <paramref name="user"/>'s NightMail-shaped token.</summary>
    public async Task<HttpClient> GraphClientAsync(string user, CancellationToken cancellationToken)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await UserTokenAsync(user, cancellationToken));
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        _backchannel.Dispose();
        await _factory.DisposeAsync();
        _stack.Dispose();
    }

    private async Task<string> UserTokenAsync(string user, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Issuer + "/protocol/openid-connect/token")
        {
            Content = new FormUrlEncodedContent(
            [
                new("grant_type", "password"),
                new("client_id", "nightware-dev"),
                new("client_secret", _stack.Env("NIGHTWARE_DEV_CLIENT_SECRET")),
                new("username", user),
                new("password", _stack.Env("TEST_USER_PASSWORD")),
                new("scope", "openid email profile"),
            ]),
        };
        using var response = await _backchannel.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("access_token").GetString()!;
    }
}

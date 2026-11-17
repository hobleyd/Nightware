using System.Globalization;
using System.Net;
using System.Security.Claims;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

using Nightware.Admin.Auth;

namespace Nightware.Admin.Tests.Auth;

/// <summary>
/// The session limits through ASP.NET Core's real cookie handler, including the sliding refreshes that
/// reissue the cookie (and with it <c>IssuedUtc</c>) while the admin keeps working.
/// </summary>
public sealed class SessionLifetimeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_active_session_still_ends_at_the_absolute_lifetime()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 17, 8, 0, 0, TimeSpan.Zero));
        using IHost host = await StartAsync(time);
        using HttpClient client = Client(host);
        string cookie = await SignInAsync(client);

        // Work steadily: a request every 15 minutes keeps the 20-minute idle window open and refreshes the cookie.
        for (int i = 0; i < 31; i++)
        {
            time.Advance(TimeSpan.FromMinutes(15));
            (HttpStatusCode status, cookie) = await CheckAsync(client, cookie);
            status.ShouldBe(HttpStatusCode.OK, $"after {(i + 1) * 15} minutes");
        }

        time.Advance(TimeSpan.FromMinutes(15)); // 8 h
        time.Advance(TimeSpan.FromMinutes(1));
        (await CheckAsync(client, cookie)).Status.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_idle_session_ends_after_the_idle_timeout()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 17, 8, 0, 0, TimeSpan.Zero));
        using IHost host = await StartAsync(time);
        using HttpClient client = Client(host);
        string cookie = await SignInAsync(client);

        time.Advance(TimeSpan.FromMinutes(21));

        (await CheckAsync(client, cookie)).Status.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_session_without_a_start_stamp_is_rejected()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 17, 8, 0, 0, TimeSpan.Zero));
        using IHost host = await StartAsync(time);
        using HttpClient client = Client(host);
        string cookie = await SignInAsync(client, stamp: false);

        (await CheckAsync(client, cookie)).Status.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static async Task<IHost> StartAsync(FakeTimeProvider time)
    {
        var admin = new AdminAuthOptions { Authority = "https://kc.example/auth/realms/x", ClientSecret = "s" };
        IHost host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services =>
                {
                    services.AddSingleton<TimeProvider>(time);
                    services.AddRouting();
                    services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(cookie =>
                    {
                        AdminAuthentication.ConfigureCookie(cookie, admin);
                        cookie.TimeProvider = time;
                        cookie.Events.OnRedirectToLogin = static c =>
                        {
                            c.Response.StatusCode = StatusCodes.Status401Unauthorized;
                            return Task.CompletedTask;
                        };
                    });
                })
                .Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/signin", async (HttpContext http, bool? stamp) =>
                        {
                            var properties = new AuthenticationProperties();
                            if (stamp != false)
                            {
                                properties.Items[AdminAuthentication.SessionStartedItem] = time.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
                            }

                            await http.SignInAsync(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "u")], "test")), properties);
                        });
                        endpoints.MapGet("/check", (HttpContext http) =>
                            http.User.Identity?.IsAuthenticated == true ? Results.Ok() : Results.Unauthorized());
                    });
                }))
            .StartAsync(Ct);
        return host;
    }

    private static HttpClient Client(IHost host)
    {
        HttpClient client = host.GetTestClient();
        client.BaseAddress = new Uri("https://localhost");
        return client;
    }

    private static async Task<string> SignInAsync(HttpClient client, bool stamp = true)
    {
        using HttpResponseMessage response = await client.GetAsync(new Uri($"/signin?stamp={stamp}", UriKind.Relative), Ct);
        return CookieFrom(response) ?? throw new InvalidOperationException("no session cookie");
    }

    private static async Task<(HttpStatusCode Status, string Cookie)> CheckAsync(HttpClient client, string cookie)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/check");
        request.Headers.Add("Cookie", cookie);
        using HttpResponseMessage response = await client.SendAsync(request, Ct);
        return (response.StatusCode, CookieFrom(response) ?? cookie);
    }

    private static string? CookieFrom(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? values)
            ? values.Select(v => v.Split(';')[0]).FirstOrDefault(v => v.StartsWith(AdminAuthentication.CookieName + "=", StringComparison.Ordinal) && v.Length > AdminAuthentication.CookieName.Length + 1)
            : null;
}

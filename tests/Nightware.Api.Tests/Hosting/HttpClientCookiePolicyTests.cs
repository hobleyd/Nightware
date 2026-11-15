using System.Collections;
using System.Reflection;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

using Nightware.Api.Hosting;
using Nightware.Api.Tests.Support;

namespace Nightware.Api.Tests.Hosting;

/// <summary>
/// No HttpClient in the façade may keep cookies: primary handlers are pooled and shared across
/// users, and Nextcloud's session cookie was once replayed on another user's request.
/// </summary>
public sealed class HttpClientCookiePolicyTests
{
    [Fact]
    public async Task Every_client_in_the_real_composition_has_a_cookieless_primary_handler()
    {
        IServiceCollection? registered = null;
        await using var host = await TestHost.StartAsync(s =>
        {
            registered = s;
            // A client added later that sets a base address but leaves the default primary handler.
            s.AddHttpClient("added-later-without-configuration", static c => c.BaseAddress = new Uri("http://backend.invalid/"));
        });
        var names = ClientNames(registered!);
        var factory = host.App.Services.GetRequiredService<IHttpMessageHandlerFactory>();

        names.Count.ShouldBeGreaterThanOrEqualTo(6, $"found only: {string.Join(", ", names)}");
        names.ShouldContain("added-later-without-configuration");
        foreach (var name in names)
        {
            var primary = Primary(factory.CreateHandler(name));
            var useCookies = primary switch
            {
                SocketsHttpHandler sockets => sockets.UseCookies,
                HttpClientHandler classic => classic.UseCookies,
                _ => throw new ShouldAssertException($"client '{name}' has an unexpected primary handler {primary.GetType().Name}"),
            };
            useCookies.ShouldBeFalse($"client '{name}' keeps cookies on its pooled handler");
        }
    }

    [Fact]
    public void The_filter_turns_cookies_off_whatever_the_handler_type()
    {
        using var sockets = new SocketsHttpHandler { UseCookies = true };
        using var classic = new HttpClientHandler { UseCookies = true };

        CookielessHttpClientsFilter.DisableCookies(sockets);
        CookielessHttpClientsFilter.DisableCookies(classic);

        sockets.UseCookies.ShouldBeFalse();
        classic.UseCookies.ShouldBeFalse();
    }

    /// <summary>Every named and typed client: from named factory options and the factory's client registry.</summary>
    private static List<string> ClientNames(IServiceCollection services)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var descriptor in services)
        {
            if (descriptor.ImplementationInstance is ConfigureNamedOptions<HttpClientFactoryOptions> { Name: { Length: > 0 } name })
            {
                names.Add(name);
            }

            // Microsoft.Extensions.Http's internal HttpClientMappingRegistry records every AddHttpClient name.
            if (descriptor.ImplementationInstance?.GetType().Name == "HttpClientMappingRegistry"
                && descriptor.ImplementationInstance.GetType().GetProperty("NamedClientRegistrations", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?.GetValue(descriptor.ImplementationInstance) is IEnumerable registry)
            {
                // Typed clients: name -> type.
                foreach (var entry in registry)
                {
                    names.Add((string)entry.GetType().GetProperty("Key")!.GetValue(entry)!);
                }
            }
        }

        return [.. names.Order(StringComparer.Ordinal)];
    }

    private static HttpMessageHandler Primary(HttpMessageHandler handler)
    {
        while (handler is DelegatingHandler { InnerHandler: { } inner })
        {
            handler = inner;
        }

        return handler;
    }
}

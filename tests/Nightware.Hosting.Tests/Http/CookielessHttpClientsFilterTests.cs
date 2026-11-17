using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

using Nightware.Hosting.Http;

namespace Nightware.Hosting.Tests.Http;

/// <summary>
/// No factory client keeps cookies: primary handlers are pooled and shared across users. The real
/// composition of each host is checked by its own <c>HttpClientCookiePolicyTests</c>.
/// </summary>
public sealed class CookielessHttpClientsFilterTests
{
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

    [Fact]
    public void Another_handler_type_is_left_alone() =>
        Should.NotThrow(() => CookielessHttpClientsFilter.DisableCookies(new StubHandler()));

    [Fact]
    public void A_registered_client_that_asks_for_cookies_gets_none()
    {
        var services = new ServiceCollection().AddCookielessHttpClients();
        services.AddHttpClient("with-cookies")
            .ConfigurePrimaryHttpMessageHandler(static () => new SocketsHttpHandler { UseCookies = true });
        services.AddHttpClient("default-handler");
        using var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IHttpMessageHandlerFactory>();

        UseCookies(factory.CreateHandler("with-cookies")).ShouldBeFalse();
        UseCookies(factory.CreateHandler("default-handler")).ShouldBeFalse();
    }

    [Fact]
    public void Registering_twice_adds_the_filter_once()
    {
        var services = new ServiceCollection().AddCookielessHttpClients().AddCookielessHttpClients();

        services.Count(static d => d.ImplementationType == typeof(CookielessHttpClientsFilter)).ShouldBe(1);
    }

    private static bool UseCookies(HttpMessageHandler handler)
    {
        while (handler is DelegatingHandler { InnerHandler: { } inner })
        {
            handler = inner;
        }

        return handler switch
        {
            SocketsHttpHandler sockets => sockets.UseCookies,
            HttpClientHandler classic => classic.UseCookies,
            _ => throw new ShouldAssertException($"unexpected primary handler {handler.GetType().Name}"),
        };
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage());
    }
}

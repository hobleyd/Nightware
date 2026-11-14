using Microsoft.Extensions.DependencyInjection;
using Nightware.Adapters.Directory.Core;
using Nightware.Adapters.Directory.Core.Admin;
using Nightware.Adapters.Directory.Tests.TestSupport;

namespace Nightware.Adapters.Directory.Tests;

/// <summary>Pooled primary handlers are shared across callers, so none may keep cookies.</summary>
public sealed class CookieFreeHandlerTests
{
    [Theory]
    [InlineData(KeycloakHttp.TokenClientName)]
    [InlineData(nameof(IKeycloakAdminClient))]
    public async Task Every_client_uses_a_cookie_free_primary_handler(string name)
    {
        await using var host = AdapterHost.Create("http://keycloak.test");
        var handler = host.Provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(name);

        Primary(handler).ShouldBeOfType<SocketsHttpHandler>().UseCookies.ShouldBeFalse();
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

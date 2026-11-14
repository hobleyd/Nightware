using Microsoft.Extensions.DependencyInjection;
using Nightware.Adapters.Jmap.Core.Http;
using Nightware.Adapters.Jmap.Tests.Support;

namespace Nightware.Adapters.Jmap.Tests;

/// <summary>
/// Pooled primary handlers are shared across callers; a cookie container on one would replay a
/// session cookie set for one user's request on another user's request.
/// </summary>
public sealed class CookieFreeHandlerTests
{
    [Theory]
    [InlineData(JmapHttpClients.Api)]
    [InlineData(JmapHttpClients.Blob)]
    [InlineData(JmapHttpClients.Health)]
    public async Task Every_client_uses_a_cookie_free_primary_handler(string name)
    {
        await using var host = new JmapTestHost();
        var handler = host.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(name);

        var primary = Primary(handler).ShouldBeOfType<SocketsHttpHandler>();

        primary.UseCookies.ShouldBeFalse();
        primary.AllowAutoRedirect.ShouldBeFalse();
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

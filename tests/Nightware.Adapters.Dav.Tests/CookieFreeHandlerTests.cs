using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Nightware.Adapters.Dav.Core;
using Nightware.Application.Common.Identity;

namespace Nightware.Adapters.Dav.Tests;

/// <summary>
/// Nextcloud answers a bearer request with a session cookie. Pooled primary handlers are shared
/// across callers, so a cookie container would replay one user's session on another user's request.
/// </summary>
public sealed class CookieFreeHandlerTests
{
    [Theory]
    [InlineData(nameof(IDavClient))]
    [InlineData(DavHealthProbe.HttpClientName)]
    public void Every_client_uses_a_cookie_free_primary_handler(string name)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => Substitute.For<IBackendCredentials>());
        services.AddDavAdapter(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Dav:BaseUrl"] = "http://nextcloud.test/",
        }).Build());
        using var provider = services.BuildServiceProvider();

        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(name);

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

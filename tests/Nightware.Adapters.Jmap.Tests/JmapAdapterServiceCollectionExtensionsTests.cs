using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nightware.Adapters.Jmap.Core;
using Nightware.Adapters.Jmap.Core.Auth;
using Nightware.Adapters.Jmap.Core.Blobs;
using Nightware.Adapters.Jmap.Core.Http;
using Nightware.Adapters.Jmap.Core.Session;
using Nightware.Adapters.Jmap.Tests.Support;
using Nightware.Application.Common.Identity;

namespace Nightware.Adapters.Jmap.Tests;

public sealed class JmapAdapterServiceCollectionExtensionsTests
{
    [Fact]
    public async Task Registers_the_core_services_with_their_lifetimes()
    {
        await using var host = new JmapTestHost();
        using var scope = host.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IJmapClient>().ShouldBeOfType<JmapClient>();
        scope.ServiceProvider.GetRequiredService<IJmapBlobService>().ShouldBeOfType<JmapBlobService>();
        scope.ServiceProvider.GetRequiredService<JmapConnectionProvider>()
            .ShouldBeSameAs(scope.ServiceProvider.GetRequiredService<JmapConnectionProvider>());
        host.Services.GetRequiredService<IJmapSessionProvider>().ShouldBeSameAs(host.Services.GetRequiredService<IJmapSessionProvider>());
    }

    [Fact]
    public async Task Every_named_client_builds_with_the_configured_pipeline()
    {
        await using var host = new JmapTestHost(s =>
        {
            s["Jmap:AttemptTimeout"] = "00:00:20";
            s["Jmap:RequestTimeout"] = "00:01:00";
            s["Jmap:BlobTimeout"] = "00:30:00";
            s["Jmap:MaxRetryAttempts"] = "3";
        });
        var factory = host.Services.GetRequiredService<IHttpClientFactory>();

        foreach (var name in new[] { JmapHttpClients.Api, JmapHttpClients.Blob, JmapHttpClients.Health })
        {
            factory.CreateClient(name).ShouldNotBeNull();
        }
    }

    [Fact]
    public void Options_bind_from_the_jmap_section_and_validate_lazily()
    {
        var services = new ServiceCollection();
        services.AddJmapAdapter(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jmap:SessionUrl"] = "http://stalwart:8080/jmap/session",
            ["Jmap:SessionCacheDuration"] = "00:01:00",
            ["Jmap:MasterUserSeparator"] = "*",
        }).Build());
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<JmapOptions>>().Value;
        options.SessionUrl.ShouldBe(new Uri("http://stalwart:8080/jmap/session"));
        options.SessionCacheDuration.ShouldBe(TimeSpan.FromMinutes(1));
        options.MasterUserSeparator.ShouldBe("*");

        var empty = new ServiceCollection();
        empty.AddJmapAdapter(new ConfigurationBuilder().Build());
        using var invalid = empty.BuildServiceProvider();
        Should.Throw<OptionsValidationException>(() => invalid.GetRequiredService<IOptions<JmapOptions>>().Value);
    }

    [Fact]
    public void Requires_configuration()
    {
        Should.Throw<ArgumentNullException>(() => new ServiceCollection().AddJmapAdapter(null!));
    }

    [Fact]
    public void Retry_predicate_only_allows_safe_requests()
    {
        using var get = new HttpRequestMessage(HttpMethod.Get, "http://x/");
        using var safePost = new HttpRequestMessage(HttpMethod.Post, "http://x/").MarkSafe();
        using var unsafePost = new HttpRequestMessage(HttpMethod.Post, "http://x/").MarkSafe(false);
        using var unmarked = new HttpRequestMessage(HttpMethod.Post, "http://x/");

        get.IsSafe().ShouldBeTrue();
        safePost.IsSafe().ShouldBeTrue();
        unsafePost.IsSafe().ShouldBeFalse();
        unmarked.IsSafe().ShouldBeFalse();
        ((HttpRequestMessage?)null).IsSafe().ShouldBeFalse();
    }

    [Fact]
    public async Task Authentication_handler_refuses_requests_without_a_credential()
    {
        using var handler = new JmapAuthenticationHandler(Options.Create(new JmapOptions())) { InnerHandler = new StubHandler() };
        using var invoker = new HttpMessageInvoker(handler);
        using var bare = new HttpRequestMessage(HttpMethod.Get, "http://x/");
        using var anonymous = new HttpRequestMessage(HttpMethod.Get, "http://x/").AsAnonymous();
        using var withCredential = new HttpRequestMessage(HttpMethod.Get, "http://x/")
            .WithCredential(new BackendCredential("Bearer", "t", CredentialStrategy.PassThrough));

        await Should.ThrowAsync<InvalidOperationException>(() => invoker.SendAsync(bare, TestContext.Current.CancellationToken));
        (await invoker.SendAsync(anonymous, TestContext.Current.CancellationToken)).RequestMessage!.Headers.Authorization.ShouldBeNull();
        (await invoker.SendAsync(withCredential, TestContext.Current.CancellationToken)).RequestMessage!.Headers.Authorization!.ToString().ShouldBe("Bearer t");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage { RequestMessage = request });
    }
}

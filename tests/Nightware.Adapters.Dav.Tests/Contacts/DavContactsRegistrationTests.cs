using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Nightware.Adapters.Dav.Contacts;
using Nightware.Adapters.Dav.Core;
using Nightware.Adapters.Dav.Directory;
using Nightware.Application.Common.Identity;
using Nightware.Application.Contacts;
using Nightware.Application.Directory;

namespace Nightware.Adapters.Dav.Tests.Contacts;

public sealed class DavContactsRegistrationTests
{
    [Fact]
    public void The_contacts_hook_registers_the_contact_store_and_the_photo_reader_per_request()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Dav:BaseUrl"] = "https://cloud.example.com/" }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => Substitute.For<IBackendCredentials>());
        services.AddDavAdapter(configuration);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IContactStore>().ShouldBeOfType<DavContactStore>();
        scope.ServiceProvider.GetRequiredService<IUserPhotoReader>().ShouldBeOfType<NextcloudAvatarReader>();
        Should.Throw<InvalidOperationException>(() => provider.GetRequiredService<IContactStore>(), "scoped: it reads the caller's credential");
    }

    [Theory]
    [InlineData(nameof(IDavClient), true)]
    [InlineData(nameof(IUserPhotoReader), false)]
    public void Nextcloud_clients_keep_no_cookies(string client, bool followsRedirects)
    {
        using var provider = Build();

        var primary = Primary(provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(client));

        // A pooled cookie jar replays Nextcloud's session cookie next to the next bearer token: 401.
        primary.UseCookies.ShouldBeFalse();
        primary.AllowAutoRedirect.ShouldBe(followsRedirects);
    }

    private static ServiceProvider Build()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Dav:BaseUrl"] = "https://cloud.example.com/" }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => Substitute.For<IBackendCredentials>());
        services.AddDavAdapter(configuration);
        return services.BuildServiceProvider();
    }

    private static SocketsHttpHandler Primary(HttpMessageHandler handler)
    {
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler!;
        }

        return handler.ShouldBeOfType<SocketsHttpHandler>();
    }
}

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Nightware.Api.Composition;

namespace Nightware.Api.Tests.Support;

/// <summary>
/// The real composition on real Kestrel (loopback, ephemeral port), for behaviour the in-memory
/// <see cref="TestHost"/> server does not implement: Kestrel limits and the server header.
/// </summary>
internal sealed class KestrelTestHost : IAsyncDisposable
{
    private KestrelTestHost(WebApplication app, Uri address)
    {
        App = app;
        Client = new HttpClient { BaseAddress = address };
    }

    public WebApplication App { get; }

    public HttpClient Client { get; }

    public static async Task<KestrelTestHost> StartAsync(IDictionary<string, string?>? configuration = null, Action<IServiceCollection>? services = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(TestHost.DefaultConfiguration());
        if (configuration is not null)
        {
            builder.Configuration.AddInMemoryCollection(configuration);
        }

        builder.Services.AddNightware(builder.Configuration);
        builder.Services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, static o =>
        {
            o.TokenValidationParameters.IssuerSigningKey = TestHost.SigningKey;
            o.TokenValidationParameters.ValidIssuer = TestHost.Issuer;
        });
        services?.Invoke(builder.Services);

        var app = builder.Build();
        app.UseNightware();
        await app.StartAsync(TestContext.Current.CancellationToken);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new KestrelTestHost(app, new Uri(address));
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.DisposeAsync();
    }
}

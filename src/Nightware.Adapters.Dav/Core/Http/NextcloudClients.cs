using System.Net.Http.Headers;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Nightware.Adapters.Dav.Core.Http;

/// <summary>Pipeline pieces shared by the Nextcloud <c>HttpClient</c>s.</summary>
public static class NextcloudClients
{
    /// <summary>
    /// <c>Nightware/&lt;version&gt;</c>, so Nextcloud's devices list and access log name the façade instead of
    /// "unknown browser" and <c>-</c>.
    /// </summary>
    public static ProductInfoHeaderValue UserAgent { get; } = new("Nightware", VersionOf(typeof(NextcloudClients).Assembly));

    /// <summary>Sends <see cref="UserAgent"/> on every request of <paramref name="client"/>.</summary>
    public static IHttpClientBuilder WithNightwareUserAgent(this IHttpClientBuilder client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client.ConfigureHttpClient(static http => http.DefaultRequestHeaders.UserAgent.Add(UserAgent));
    }

    /// <summary>
    /// Adds <see cref="NextcloudSessionHandler"/>. Call it after <see cref="DavAuthenticationHandler"/>, so the
    /// session handler runs inside it and sees each attempt's credential.
    /// </summary>
    public static IHttpClientBuilder WithNextcloudSession(this IHttpClientBuilder client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client.AddHttpMessageHandler<NextcloudSessionHandler>();
    }

    internal static string VersionOf(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return informational?.Split('+')[0] is { Length: > 0 } version ? version : assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}

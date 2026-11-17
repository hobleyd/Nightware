using System.Text;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Nightware.Adapters.Dav.Administration;

using WireMock.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Nightware.Adapters.Dav.Tests.Administration;

/// <summary>
/// A WireMock Nextcloud plus a service provider built by the real <see cref="DavAdministrationServiceCollectionExtensions.AddNextcloudAdministration"/>.
/// Answers are shaped as Nextcloud's <c>V1Response</c>/<c>V2Response</c> render them.
/// </summary>
internal sealed class NextcloudAdminServer : IDisposable
{
    public const string Username = "nightware-admin";
    public const string Password = "s3cret-Pa55:word";

    public static readonly string BasicHeader = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Password}"));

    private ServiceProvider? _provider;

    public NextcloudAdminServer(string basePath = "", Dictionary<string, string?>? settings = null)
    {
        Server = WireMockServer.Start();
        BasePath = basePath;
        var values = new Dictionary<string, string?>
        {
            ["NextcloudProvisioning:BaseUrl"] = Server.Url + basePath + "/",
            ["NextcloudProvisioning:Username"] = Username,
            ["NextcloudProvisioning:Password"] = Password,
            ["NextcloudProvisioning:OidcProviderId"] = "2",
            ["NextcloudProvisioning:AttemptTimeout"] = "00:00:02",
            ["NextcloudProvisioning:TotalTimeout"] = "00:00:05",
        };
        foreach (var (key, value) in settings ?? [])
        {
            values[key] = value;
        }

        Configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    public WireMockServer Server { get; }

    public string BasePath { get; }

    public IConfiguration Configuration { get; }

    public CapturingLoggerProvider Logs { get; } = new();

    public ServiceProvider Provider => _provider ??= Build();

    public IEnumerable<ILogEntry> Requests => Server.LogEntries;

    public T Get<T>()
        where T : notnull => Provider.GetRequiredService<T>();

    /// <summary>Answers <paramref name="method"/> <paramref name="path"/> (below the base path) with an OCS envelope.</summary>
    public void Answer(string method, string path, int httpStatus, int ocsStatus, string data = "[]", string? message = null) =>
        AnswerRaw(method, path, httpStatus, Envelope(ocsStatus, data, message));

    public void AnswerRaw(string method, string path, int httpStatus, string body, string contentType = "application/json; charset=utf-8") =>
        Server.Given(Request.Create().WithPath(BasePath + path).UsingMethod(method))
            .RespondWith(Response.Create().WithStatusCode(httpStatus).WithHeader("Content-Type", contentType).WithBody(body));

    public static string Envelope(int ocsStatus, string data = "[]", string? message = null)
    {
        var ok = ocsStatus is 100 or 200;
        var text = ok ? "\"OK\"" : message is null ? "null" : System.Text.Json.JsonSerializer.Serialize(message);
        var status = ok ? "ok" : "failure";
        return "{\"ocs\":{\"meta\":{\"status\":\"" + status + "\",\"statuscode\":" + ocsStatus + ",\"message\":" + text + "},\"data\":" + data + "}}";
    }

    public void Dispose()
    {
        _provider?.Dispose();
        Server.Stop();
        Server.Dispose();
    }

    private ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs));
        services.AddNextcloudAdministration(Configuration);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
}

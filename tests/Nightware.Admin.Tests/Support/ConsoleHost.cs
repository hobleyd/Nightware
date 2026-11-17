using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using Nightware.Admin.Auth;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Admin.Tests.Support;

/// <summary>
/// The real console with OIDC replaced by a header-driven test scheme and every backend port substituted.
/// Headers: <c>X-Test-Sub</c> (signed in as), <c>X-Test-Auth-Age</c> (seconds since sign-in), <c>X-Test-Acr</c>.
/// </summary>
public sealed partial class ConsoleHost : WebApplicationFactory<Program>
{
    public const string Acme = "e-acme";
    public const string Other = "e-other";
    public const string GlobalAdmin = "u-global";
    public const string EntityAdmin = "u-eadmin";
    public const string Nobody = "u-nobody";

    public static readonly DateTimeOffset Now = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    public ConsoleHost()
    {
        Entities.GetAsync(Acme, Arg.Any<CancellationToken>()).Returns(AcmeEntity);
        Entities.GetAsync(Other, Arg.Any<CancellationToken>()).Returns(OtherEntity);
        Entities.ListAsync(Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<Entity>>.Success([AcmeEntity, OtherEntity]));
        Entities.ListMembersAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<IdentityAccount>>.Success([]));
        Entities.ListAdminsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<IdentityAccount>>.Success([]));
        Entities.GetMembershipsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<string>>.Success([]));
        Entities.AddMemberAsync(default!, default!, default).ReturnsForAnyArgs(Result.Success());

        Scopes.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AdminScope(false, new HashSet<string>()));
        Scopes.ResolveAsync(GlobalAdmin, Arg.Any<CancellationToken>()).Returns(new AdminScope(true, new HashSet<string>()));
        Scopes.ResolveAsync(EntityAdmin, Arg.Any<CancellationToken>()).Returns(new AdminScope(false, new HashSet<string> { Acme }));

        Identities.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Error.NotFound("no user"));
        Identities.FindByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Error.NotFound("no user"));
        Identities.FindByUsernameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Error.NotFound("no user"));
        Identities.CreateAsync(default!, default).ReturnsForAnyArgs(c => c.Arg<NewIdentity?>() is { } n
            ? Result<IdentityAccount>.Success(new IdentityAccount("u-new", n.Username, n.Email.Value, true) { FirstName = n.FirstName, LastName = n.LastName })
            : default);
        Identities.SetTemporaryPasswordAsync(default!, default!, default).ReturnsForAnyArgs(Result.Success());
        Identities.SetEnabledAsync(default!, default, default).ReturnsForAnyArgs(Result.Success());
        Identities.EndSessionsAsync(default!, default).ReturnsForAnyArgs(Result.Success());
        Identities.SetDeletionMarkerAsync(default!, default, default, default).ReturnsForAnyArgs(Result.Success());

        Mail.FindByAddressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Error.NotFound("no mailbox"));
        Mail.CreateAsync(default!, default!, default).ReturnsForAnyArgs(c => c.Arg<MailAddress?>() is { } a ? Result<MailAccount>.Success(new MailAccount("m-new", a.Value, true)) : default);
        Mail.GetUsageAsync(default!, default).ReturnsForAnyArgs(Result<long>.Success(5 * 1048576));
        Mail.DestroyAsync(default!, default).ReturnsForAnyArgs(Result.Success());
        MailDomains.FindAsync(default!, default).ReturnsForAnyArgs(c => c.Arg<MailDomain?>() is { } d ? Result<MailDomainInfo>.Success(new MailDomainInfo("d", d, true)) : default);
        MailDomains.ReloadSettingsAsync(default).ReturnsForAnyArgs(Result.Success());
        Groupware.FindAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Error.NotFound("no user"));
        Groupware.PreProvisionAsync(default!, default!, default!, default).ReturnsForAnyArgs(Result.Success());
        Groupware.SetEnabledAsync(default!, default, default).ReturnsForAnyArgs(Result.Success());

        foreach (BackendKind kind in Enum.GetValues<BackendKind>())
        {
            IBackendStatus status = Substitute.For<IBackendStatus>();
            status.CheckAsync(Arg.Any<CancellationToken>()).Returns(new BackendHealth(kind, true));
            Statuses.Add(status);
        }

        ClientOptions.BaseAddress = new Uri("https://localhost");
        ClientOptions.AllowAutoRedirect = false;
    }

    public static Entity AcmeEntity { get; } = new(Acme, "acme", "Acme Pty", [Domain("acme.example")]);

    public static Entity OtherEntity { get; } = new(Other, "other", "Other Co", [Domain("other.example")]);

    public IEntityRegistry Entities { get; } = Substitute.For<IEntityRegistry>();

    public IIdentityAccounts Identities { get; } = Substitute.For<IIdentityAccounts>();

    public IAdminScopeSource Scopes { get; } = Substitute.For<IAdminScopeSource>();

    public IMailAccounts Mail { get; } = Substitute.For<IMailAccounts>();

    public IMailDomains MailDomains { get; } = Substitute.For<IMailDomains>();

    public IGroupwareAccounts Groupware { get; } = Substitute.For<IGroupwareAccounts>();

    public List<IBackendStatus> Statuses { get; } = [];

    public FakeTimeProvider Time { get; } = new(Now);

    public CapturingLoggerProvider Logs { get; } = new();

    public bool EntityAdminsMayDeleteData { get; set; }

    public static MailDomain Domain(string name) => MailDomain.TryParse(name, out MailDomain? d) ? d : throw new ArgumentException(name);

    /// <summary>A Keycloak user in <paramref name="entityId"/>.</summary>
    public IdentityAccount AddUser(string id, string username, string email, bool enabled = true, string entityId = Acme)
    {
        var user = new IdentityAccount(id, username, email, enabled);
        Identities.GetAsync(id, Arg.Any<CancellationToken>()).Returns(user);
        Identities.FindByEmailAsync(email, Arg.Any<CancellationToken>()).Returns(user);
        Identities.FindByUsernameAsync(username, Arg.Any<CancellationToken>()).Returns(user);
        Entities.GetMembershipsAsync(id, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<string>>.Success([entityId]));
        return user;
    }

    public HttpClient ClientAs(string sub, TimeSpan? authAge = null, string? acr = "mfa")
    {
        HttpClient client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.SubHeader, sub);
        client.DefaultRequestHeaders.Add(TestAuthHandler.AuthAgeHeader, ((int)(authAge ?? TimeSpan.FromMinutes(1)).TotalSeconds).ToString(CultureInfo.InvariantCulture));
        if (acr is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.AcrHeader, acr);
        }

        return client;
    }

    /// <summary>GETs <paramref name="pageUrl"/> and POSTs the form fields with its antiforgery token.</summary>
    public static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string pageUrl, string postUrl, IDictionary<string, string> fields)
    {
        using HttpResponseMessage page = await client.GetAsync(new Uri(pageUrl, UriKind.Relative), TestContext.Current.CancellationToken);
        page.StatusCode.ShouldBe(HttpStatusCode.OK, $"GET {pageUrl}");
        string html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        string token = AntiforgeryToken().Match(html).Groups[1].Value;
        token.ShouldNotBeNullOrEmpty();

        var form = new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token) };
        using var content = new FormUrlEncodedContent(form);
        return await client.PostAsync(new Uri(postUrl, UriKind.Relative), content, TestContext.Current.CancellationToken);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Admin:Authority", "https://kc.example/auth/realms/nightware");
        builder.UseSetting("Admin:ClientSecret", "test-secret");
        builder.UseSetting("Admin:AcceptedAcrValues:0", "mfa");
        builder.UseSetting("Admin:EntityAdminsMayDeleteData", EntityAdminsMayDeleteData ? "true" : "false");
        // The real adapters are registered and validated on start; the tests replace their ports below.
        builder.UseSetting("NextcloudProvisioning:BaseUrl", "https://cloud.example/cloud");
        builder.UseSetting("NextcloudProvisioning:Username", "nightware-admin");
        builder.UseSetting("NextcloudProvisioning:Password", "nc-test-password");
        builder.UseSetting("StalwartManagement:ApiUrl", "https://mail.example/jmap");
        builder.UseSetting("StalwartManagement:Username", "nightware-admin@acme.example");
        builder.UseSetting("StalwartManagement:Password", "stalwart-test-password");
        builder.UseSetting("Directory:KeycloakBaseUrl", "https://kc.example/auth");
        builder.UseSetting("Directory:Realm", "nightware");
        builder.UseSetting("Directory:ClientId", "nightware-admin");
        builder.UseSetting("Directory:ClientSecret", "kc-test-secret");
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(TestAuthHandler.SchemeName).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, null);
            services.PostConfigure<AuthenticationOptions>(o =>
            {
                o.DefaultScheme = TestAuthHandler.SchemeName;
                o.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                o.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                o.DefaultForbidScheme = TestAuthHandler.SchemeName;
            });
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
            services.AddSingleton(Entities).AddSingleton(Identities).AddSingleton(Scopes).AddSingleton(Mail).AddSingleton(MailDomains).AddSingleton(Groupware);
            services.RemoveAll<IBackendStatus>();
            foreach (IBackendStatus status in Statuses)
            {
                services.AddSingleton(status);
            }
        });
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" type=\"hidden\" value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryToken();

    /// <summary>Authenticates from test headers; a challenge is a 401 and a forbid a 403.</summary>
    public sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, TimeProvider time)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Test";
        public const string SubHeader = "X-Test-Sub";
        public const string AuthAgeHeader = "X-Test-Auth-Age";
        public const string AcrHeader = "X-Test-Acr";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(SubHeader, out var sub))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            int age = int.Parse(Request.Headers[AuthAgeHeader].ToString() is { Length: > 0 } a ? a : "60", CultureInfo.InvariantCulture);
            var claims = new List<Claim>
            {
                new("sub", sub.ToString()),
                new("preferred_username", sub.ToString()),
                new(AdminAuthentication.AuthTimeClaim, (time.GetUtcNow().ToUnixTimeSeconds() - age).ToString(CultureInfo.InvariantCulture)),
            };
            if (Request.Headers.TryGetValue(AcrHeader, out var acr))
            {
                claims.Add(new Claim(AdminAuthentication.AcrClaim, acr.ToString()));
            }

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName, "preferred_username", "role"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
        }
    }
}

public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();

    public IReadOnlyCollection<string> Lines => _lines;

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _lines);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, System.Collections.Concurrent.ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue($"{category}|{eventId.Name}|{formatter(state, exception)}|{exception}");
    }
}

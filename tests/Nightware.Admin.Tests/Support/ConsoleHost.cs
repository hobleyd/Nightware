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
using Nightware.Admin.Hosting;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;

using Group = Nightware.Domain.Administration.Group;
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
        PublicDns.ResolveAsync(default!, default).ReturnsForAnyArgs(Error.NotFound("not in public DNS"));
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
        Identities.MarkEmailVerifiedAsync(default!, default).ReturnsForAnyArgs(Result.Success());
        Identities.EndSessionsAsync(default!, default).ReturnsForAnyArgs(Result.Success());
        Identities.SetDeletionMarkerAsync(default!, default, default, default).ReturnsForAnyArgs(Result.Success());
        Identities.AddAccessEventAsync(default!, default!, default).ReturnsForAnyArgs(Result.Success());

        Mail.FindByAddressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Error.NotFound("no mailbox"));
        Mail.CreateAsync(default!, default!, default).ReturnsForAnyArgs(c => c.Arg<MailAddress?>() is { } a ? Result<MailAccount>.Success(new MailAccount("m-new", a.Value, true)) : default);
        Mail.GetUsageAsync(default!, default).ReturnsForAnyArgs(Result<long>.Success(5 * 1048576));
        Mail.DestroyAsync(default!, default).ReturnsForAnyArgs(Result.Success());
        MailDomains.FindAsync(default!, default).ReturnsForAnyArgs(c => c.Arg<MailDomain?>() is { } d ? Result<MailDomainInfo>.Success(new MailDomainInfo("d", d, true)) : default);
        MailDomains.ReloadSettingsAsync(default).ReturnsForAnyArgs(Result.Success());
        Groupware.FindAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Error.NotFound("no user"));
        Groupware.PreProvisionAsync(default!, default!, default!, default).ReturnsForAnyArgs(Result.Success());
        Groupware.SetEnabledAsync(default!, default, default).ReturnsForAnyArgs(Result.Success());

        Groups.ListAsync(default!, default).ReturnsForAnyArgs(Result<IReadOnlyList<Group>>.Success([]));
        Groups.GetAsync(default!, default!, default).ReturnsForAnyArgs(Error.NotFound("no group"));
        Groups.ListMembersAsync(default!, default!, default).ReturnsForAnyArgs(Result<IReadOnlyList<IdentityAccount>>.Success([]));
        Groups.ListUserGroupsAsync(default!, default!, default).ReturnsForAnyArgs(Result<IReadOnlyList<Group>>.Success([]));
        Groups.CreateAsync(default!, default!, default).ReturnsForAnyArgs(c =>
            c.Arg<NewGroup?>() is { } g ? Result<Group>.Success(new Group("g-new", c.Arg<string>(), g.Key, g.Name) { Mail = g.Mail, Files = g.Files }) : default);
        Groups.UpdateAsync(default!, default).ReturnsForAnyArgs(c => c.Arg<Group?>() is { } g ? Result<Group>.Success(g) : default);
        Groups.DeleteAsync(default!, default!, default).ReturnsForAnyArgs(Result.Success());
        Groups.AddMemberAsync(default!, default!, default!, default).ReturnsForAnyArgs(Result.Success());
        Groups.RemoveMemberAsync(default!, default!, default!, default).ReturnsForAnyArgs(Result.Success());
        Lists.FindByAddressAsync(default!, default).ReturnsForAnyArgs(Error.NotFound("no list"));
        Lists.CreateAsync(default!, default!, default!, default!, default).ReturnsForAnyArgs(c => c.Arg<MailAddress?>() is { } a ? Result<MailingList>.Success(new MailingList("l-new", a.Value)) : default);
        Lists.UpdateAsync(default!, default!, default!, default).ReturnsForAnyArgs(Result.Success());
        Lists.DestroyAsync(default!, default).ReturnsForAnyArgs(Result.Success());
        Lists.SetSenderRuleAsync(default!, default, default).ReturnsForAnyArgs(Result.Success());
        GroupwareGroups.FindAsync(default!, default).ReturnsForAnyArgs(Error.NotFound("no group"));
        GroupwareGroups.CreateAsync(default!, default!, default).ReturnsForAnyArgs(Result.Success());
        GroupwareGroups.SetDisplayNameAsync(default!, default!, default).ReturnsForAnyArgs(Result.Success());
        GroupwareGroups.AddMemberAsync(default!, default!, default).ReturnsForAnyArgs(Result.Success());
        GroupwareGroups.RemoveMemberAsync(default!, default!, default).ReturnsForAnyArgs(Result.Success());
        GroupwareGroups.DeleteAsync(default!, default).ReturnsForAnyArgs(Result.Success());
        GroupwareGroups.FindFolderAsync(default, default).ReturnsForAnyArgs(Error.NotFound("no folder"));
        GroupwareGroups.CreateFolderAsync(default!, default).ReturnsForAnyArgs(c => c.Arg<string?>() is { } m ? Result<TeamFolder>.Success(new TeamFolder(7, m, new HashSet<string>())) : default);
        GroupwareGroups.GrantFolderAsync(default, default!, default).ReturnsForAnyArgs(Result.Success());
        GroupwareGroups.DeleteFolderAsync(default, default).ReturnsForAnyArgs(Result.Success());
        SharedCalendars.FindAsync(default!, default).ReturnsForAnyArgs(Error.NotFound("no calendar"));
        SharedCalendars.EnsureAsync(default!, default!, default!, default).ReturnsForAnyArgs(Result.Success());
        SharedCalendars.DeleteAsync(default!, default).ReturnsForAnyArgs(Result.Success());
        Mailboxes.ListAsync(default!, default).ReturnsForAnyArgs(Result<IReadOnlyList<SharedMailbox>>.Success([]));
        Mailboxes.GetAsync(default!, default!, default).ReturnsForAnyArgs(Error.NotFound("no shared mailbox"));
        Mailboxes.ListMembersAsync(default!, default!, default).ReturnsForAnyArgs(Result<IReadOnlyList<IdentityAccount>>.Success([]));
        Mailboxes.ListUserMailboxesAsync(default!, default!, default).ReturnsForAnyArgs(Result<IReadOnlyList<SharedMailbox>>.Success([]));
        Mailboxes.CreateAsync(default!, default!, default).ReturnsForAnyArgs(c =>
            c.Arg<NewSharedMailbox?>() is { } m ? Result<SharedMailbox>.Success(new SharedMailbox("s-new", c.Arg<string>(), m.Address, m.Name) { Aliases = m.Aliases }) : default);
        Mailboxes.UpdateAsync(default!, default).ReturnsForAnyArgs(c => c.Arg<SharedMailbox?>() is { } m ? Result<SharedMailbox>.Success(m) : default);
        Mailboxes.DeleteAsync(default!, default!, default).ReturnsForAnyArgs(Result.Success());
        Mailboxes.AddMemberAsync(default!, default!, default!, default).ReturnsForAnyArgs(Result.Success());
        Mailboxes.RemoveMemberAsync(default!, default!, default!, default).ReturnsForAnyArgs(Result.Success());
        SharedMail.FindByAddressAsync(default!, default).ReturnsForAnyArgs(Error.NotFound("no shared mailbox"));
        SharedMail.CreateAsync(default!, default!, default!, default).ReturnsForAnyArgs(c => c.Arg<MailAddress?>() is { } a ? Result<SharedMailAccount>.Success(new SharedMailAccount("sm-new", a.Value)) : default);
        SharedMail.UpdateAsync(default!, default!, default!, default).ReturnsForAnyArgs(Result.Success());
        SharedMail.AddMemberAsync(default!, default!, default).ReturnsForAnyArgs(Result.Success());
        SharedMail.RemoveMemberAsync(default!, default!, default).ReturnsForAnyArgs(Result.Success());
        SharedMail.DestroyAsync(default!, default).ReturnsForAnyArgs(Result.Success());

        foreach (BackendKind kind in Enum.GetValues<BackendKind>())
        {
            IBackendStatus status = Substitute.For<IBackendStatus>();
            status.CheckAsync(Arg.Any<CancellationToken>()).Returns(new BackendHealth(kind, true));
            Statuses.Add(status);
        }

        Grafana.CheckAsync(Arg.Any<CancellationToken>()).Returns(new GrafanaHealth(true));

        ClientOptions.BaseAddress = new Uri("https://localhost");
        ClientOptions.AllowAutoRedirect = false;
    }

    public static Entity AcmeEntity { get; } = new(Acme, "acme", "Acme Pty", [Domain("acme.example")]);

    public static Entity OtherEntity { get; } = new(Other, "other", "Other Co", [Domain("other.example")]);

    public IEntityRegistry Entities { get; } = Substitute.For<IEntityRegistry>();

    public IGrafanaStatus Grafana { get; } = Substitute.For<IGrafanaStatus>();

    public IIdentityAccounts Identities { get; } = Substitute.For<IIdentityAccounts>();

    public IAdminScopeSource Scopes { get; } = Substitute.For<IAdminScopeSource>();

    public IMailAccounts Mail { get; } = Substitute.For<IMailAccounts>();

    public IMailDomains MailDomains { get; } = Substitute.For<IMailDomains>();

    public IGroupwareAccounts Groupware { get; } = Substitute.For<IGroupwareAccounts>();

    public IEntityGroups Groups { get; } = Substitute.For<IEntityGroups>();

    public IMailingLists Lists { get; } = Substitute.For<IMailingLists>();

    public IGroupwareGroups GroupwareGroups { get; } = Substitute.For<IGroupwareGroups>();

    public ISharedCalendars SharedCalendars { get; } = Substitute.For<ISharedCalendars>();

    public IEntityMailboxes Mailboxes { get; } = Substitute.For<IEntityMailboxes>();

    public ISharedMailAccounts SharedMail { get; } = Substitute.For<ISharedMailAccounts>();

    public IDnsHosting DnsHosting { get; } = Substitute.For<IDnsHosting>();

    /// <summary>Public DNS; answers nothing unless a test arranges it, so no test reaches the real resolver.</summary>
    public IPublicDns PublicDns { get; } = Substitute.For<IPublicDns>();

    /// <summary><c>Admin:PublicHost</c>; unset unless a test sets it before the first client.</summary>
    public string? PublicHost { get; set; }

    /// <summary>Further configuration, applied before the first client.</summary>
    public Dictionary<string, string> Settings { get; } = [];

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
        if (PublicHost is not null)
        {
            builder.UseSetting("Admin:PublicHost", PublicHost);
        }

        foreach ((string key, string value) in Settings)
        {
            builder.UseSetting(key, value);
        }

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
            services.AddSingleton(Entities).AddSingleton(Identities).AddSingleton(Scopes).AddSingleton(Mail).AddSingleton(MailDomains).AddSingleton(Groupware).AddSingleton(DnsHosting).AddSingleton(PublicDns);
            services.AddSingleton(Groups).AddSingleton(Lists).AddSingleton(GroupwareGroups).AddSingleton(Mailboxes).AddSingleton(SharedMail).AddSingleton(SharedCalendars);
            services.RemoveAll<IGrafanaStatus>();
            services.AddSingleton(Grafana);
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
        public const string EmailHeader = "X-Test-Email";

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

            if (Request.Headers.TryGetValue(EmailHeader, out var email))
            {
                claims.Add(new Claim("email", email.ToString()));
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

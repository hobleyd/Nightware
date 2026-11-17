using System.Security.Claims;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

using Nightware.Admin.Auth;
using Nightware.Admin.Composition;
using Nightware.Admin.Hosting;
using Nightware.Application.Administration;

namespace Nightware.Admin.Tests.Auth;

public sealed class AdminAuthenticationTests
{
    private static AdminAuthOptions Options(params string[] acr)
    {
        var options = new AdminAuthOptions { Authority = "https://kc.example/auth/realms/nightware", ClientSecret = "s" };
        foreach (string value in acr)
        {
            options.AcceptedAcrValues.Add(value);
        }

        return options;
    }

    [Fact]
    public void Oidc_uses_code_pkce_and_keeps_no_tokens()
    {
        var oidc = new OpenIdConnectOptions();
        AdminAuthentication.ConfigureOidc(oidc, Options("mfa"));

        oidc.ResponseType.ShouldBe(OpenIdConnectResponseType.Code);
        oidc.UsePkce.ShouldBeTrue();
        oidc.SaveTokens.ShouldBeFalse();
        oidc.MapInboundClaims.ShouldBeFalse();
        oidc.ClientId.ShouldBe("nightware-admin");
        oidc.TokenValidationParameters.ValidAudience.ShouldBe("nightware-admin");
        oidc.Scope.ShouldBe(["openid", "profile", "email"]);
        // ASP.NET deletes acr and auth_time by default; the MFA and freshness checks need them in the session.
        var json = System.Text.Json.JsonElement.Parse("""{"acr":"mfa","auth_time":1700000000,"nonce":"n"}""");
        var identity = new ClaimsIdentity([new Claim("acr", "mfa"), new Claim("auth_time", "1700000000"), new Claim("nonce", "n")]);
        foreach (var action in oidc.ClaimActions)
        {
            action.Run(json, identity, "issuer");
        }

        identity.FindFirst("acr").ShouldNotBeNull();
        identity.FindFirst("auth_time").ShouldNotBeNull();
    }

    [Fact]
    public void The_session_cookie_is_secure_http_only_and_idles_out()
    {
        var cookie = new CookieAuthenticationOptions();
        AdminAuthOptions admin = Options();
        AdminAuthentication.ConfigureCookie(cookie, admin);

        cookie.Cookie.Name.ShouldBe("__Secure-nightware.console");
        cookie.Cookie.HttpOnly.ShouldBeTrue();
        cookie.Cookie.SecurePolicy.ShouldBe(CookieSecurePolicy.Always);
        cookie.Cookie.SameSite.ShouldBe(SameSiteMode.Lax);
        cookie.ExpireTimeSpan.ShouldBe(admin.IdleTimeout);
        cookie.SlidingExpiration.ShouldBeTrue();
    }

    [Theory]
    [InlineData(7, false)]
    [InlineData(9, true)]
    public async Task A_session_ends_at_its_absolute_lifetime_however_active(int hoursOld, bool rejected)
    {
        var cookie = new CookieAuthenticationOptions();
        AdminAuthentication.ConfigureCookie(cookie, Options());
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 17, 18, 0, 0, TimeSpan.Zero));
        var http = new DefaultHttpContext { RequestServices = Services(time) };
        // IssuedUtc is fresh (a sliding refresh renews it); only the session's start stamp is old.
        var properties = new AuthenticationProperties { IssuedUtc = time.GetUtcNow() };
        properties.Items[AdminAuthentication.SessionStartedItem] = time.GetUtcNow().AddHours(-hoursOld).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var context = new CookieValidatePrincipalContext(http, new AuthenticationScheme("Cookies", null, typeof(CookieAuthenticationHandler)), cookie, new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity("x")), properties, "Cookies"));

        await cookie.Events.OnValidatePrincipal(context);

        (context.Principal is null).ShouldBe(rejected);
    }

    [Fact]
    public async Task A_sign_in_without_the_required_acr_fails()
    {
        var oidc = new OpenIdConnectOptions();
        AdminAuthentication.ConfigureOidc(oidc, Options("mfa"));

        TokenValidatedContext weak = Validated(new Claim("acr", "pwd"));
        await oidc.Events.OnTokenValidated(weak);
        weak.Result!.Failure.ShouldNotBeNull();

        TokenValidatedContext strong = Validated(new Claim("acr", "mfa"), new Claim("auth_time", "1700000000"));
        await oidc.Events.OnTokenValidated(strong);
        strong.Result.ShouldBeNull();
        strong.Properties!.Items.ShouldContainKey(AdminAuthentication.SessionStartedItem);
    }

    [Fact]
    public async Task Reauthentication_asks_for_a_fresh_login_with_the_acr()
    {
        var oidc = new OpenIdConnectOptions();
        AdminAuthentication.ConfigureOidc(oidc, Options("mfa"));
        var properties = new AuthenticationProperties();
        properties.Items[AdminAuthentication.ForceReauthenticationItem] = "1";
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));
        var context = new RedirectContext(new DefaultHttpContext { RequestServices = Services(time) }, Scheme(), oidc, properties) { ProtocolMessage = new OpenIdConnectMessage() };

        await oidc.Events.OnRedirectToIdentityProvider(context);

        context.ProtocolMessage.MaxAge.ShouldBe("0");
        context.ProtocolMessage.Prompt.ShouldBe("login");
        context.ProtocolMessage.AcrValues.ShouldBe("mfa");
        properties.Items[AdminAuthentication.ForceReauthenticationItem].ShouldBe(time.GetUtcNow().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData(null, new string[0], true)]
    [InlineData("pwd", new[] { "mfa" }, false)]
    [InlineData(null, new[] { "mfa" }, false)]
    [InlineData("mfa", new[] { "mfa", "hwk" }, true)]
    public void AcrValidator(string? acr, string[] accepted, bool ok) =>
        Admin.Auth.AcrValidator.IsAccepted(acr, accepted).ShouldBe(ok);

    [Fact]
    public void Production_refuses_to_start_without_mfa()
    {
        var production = new AdminAuthOptionsValidator(Environment(Environments.Production));
        var development = new AdminAuthOptionsValidator(Environment(Environments.Development));

        production.Validate(null, Options()).Failed.ShouldBeTrue();
        production.Validate(null, Options("mfa")).Succeeded.ShouldBeTrue();
        development.Validate(null, Options()).Succeeded.ShouldBeTrue();
        development.Validate(null, new AdminAuthOptions()).FailureMessage!.ShouldContain("Admin:Authority");
        AdminAuthOptions idleTooLong = Options();
        idleTooLong.IdleTimeout = TimeSpan.FromHours(9);
        development.Validate(null, idleTooLong).Failed.ShouldBeTrue();
    }

    [Fact]
    public void The_csp_allows_forms_to_the_console_and_keycloak_only()
    {
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Admin:Authority"] = "https://kc.example:8443/auth/realms/x" }).Build();

        string csp = AdminComposition.ContentSecurityPolicy(configuration);

        csp.ShouldContain("form-action 'self' https://kc.example:8443;");
        csp.ShouldContain("default-src 'none'");
        csp.ShouldNotContain("script-src");
    }

    [Fact]
    public void Temporary_passwords_are_long_varied_and_unique()
    {
        var generator = new RandomPasswordGenerator();
        string[] passwords = [.. Enumerable.Range(0, 200).Select(_ => generator.Generate().Password)];

        passwords.ShouldAllBe(p => p.Length == 20 && p.Any(char.IsUpper) && p.Any(char.IsLower) && p.Any(char.IsDigit) && p.Any(c => !char.IsLetterOrDigit(c)));
        passwords.Distinct().Count().ShouldBe(200);
        passwords.ShouldAllBe(p => !p.Contains('O') && !p.Contains('0') && !p.Contains('l') && !p.Contains('1'));
    }

    [Fact]
    public void The_audit_log_writes_one_structured_event()
    {
        var logs = new Support.CapturingLoggerProvider();
        using var factory = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.AddProvider(logs));
        var audit = new LoggerAuditLog(factory.CreateLogger<LoggerAuditLog>());

        audit.Record(new AuditEvent("user.offboarded", "u-a", true) { TargetUserId = "u-b", EntityId = "e", Details = new Dictionary<string, string?> { ["k"] = "v", ["empty"] = null } });

        string line = logs.Lines.Single();
        line.ShouldContain("|admin.audit|");
        line.ShouldContain("admin.audit user.offboarded by u-a succeeded=True target=u-b entity=e k=v");
        line.ShouldNotContain("empty=");
    }

    private static TokenValidatedContext Validated(params Claim[] claims)
    {
        var http = new DefaultHttpContext { RequestServices = Services(TimeProvider.System) };
        return new TokenValidatedContext(http, Scheme(), new OpenIdConnectOptions(), new ClaimsPrincipal(new ClaimsIdentity(claims, "oidc")), new AuthenticationProperties());
    }

    private static AuthenticationScheme Scheme() => new(OpenIdConnectDefaults.AuthenticationScheme, null, typeof(OpenIdConnectHandler));

    private static ServiceProvider Services(TimeProvider time) =>
        new ServiceCollection().AddSingleton(time).AddSingleton<IAuthenticationService>(Substitute.For<IAuthenticationService>()).AddLogging(b => b.AddProvider(NullLoggerProvider.Instance)).BuildServiceProvider();

    private static IHostEnvironment Environment(string name)
    {
        IHostEnvironment environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(name);
        return environment;
    }

    [Theory]
    [InlineData(-3600, true)]
    [InlineData(-30, false)]
    [InlineData(5, false)]
    public async Task A_forced_reauthentication_must_be_newer_than_the_request(int authTimeOffsetSeconds, bool refused)
    {
        var oidc = new OpenIdConnectOptions();
        AdminAuthentication.ConfigureOidc(oidc, Options("mfa"));
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 17, 12, 0, 0, TimeSpan.Zero));
        var http = new DefaultHttpContext { RequestServices = Services(time) };
        var properties = new AuthenticationProperties();
        properties.Items[AdminAuthentication.ForceReauthenticationItem] = time.GetUtcNow().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        string authTime = time.GetUtcNow().AddSeconds(authTimeOffsetSeconds).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var context = new TokenValidatedContext(http, Scheme(), new OpenIdConnectOptions(),
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("acr", "mfa"), new Claim(AdminAuthentication.AuthTimeClaim, authTime)], "oidc")), properties);

        await oidc.Events.OnTokenValidated(context);

        (context.Result?.Failure is not null).ShouldBe(refused);
    }

    [Fact]
    public async Task A_token_without_auth_time_is_refused()
    {
        var oidc = new OpenIdConnectOptions();
        AdminAuthentication.ConfigureOidc(oidc, Options("mfa"));
        TokenValidatedContext context = Validated(new Claim("acr", "mfa"));

        await oidc.Events.OnTokenValidated(context);

        context.Result!.Failure.ShouldNotBeNull();
    }

    [Fact]
    public async Task An_ordinary_sign_in_keeps_keycloaks_auth_time()
    {
        var oidc = new OpenIdConnectOptions();
        AdminAuthentication.ConfigureOidc(oidc, Options("mfa"));
        TokenValidatedContext context = Validated(new Claim("acr", "mfa"), new Claim(AdminAuthentication.AuthTimeClaim, "1700000000"));

        await oidc.Events.OnTokenValidated(context);

        context.Principal!.FindFirst(AdminAuthentication.AuthTimeClaim)!.Value.ShouldBe("1700000000");
    }

    [Fact]
    public async Task The_id_token_from_the_token_endpoint_is_kept_for_sign_out_and_nothing_else()
    {
        var oidc = new OpenIdConnectOptions();
        AdminAuthentication.ConfigureOidc(oidc, Options("mfa"));
        TokenValidatedContext context = Validated(new Claim("acr", "mfa"), new Claim("auth_time", "1700000000"));
        context.TokenEndpointResponse = new OpenIdConnectMessage { IdToken = "id.token.value", AccessToken = "access", RefreshToken = "refresh" };

        await oidc.Events.OnTokenValidated(context);

        context.Properties!.GetTokens().Select(t => t.Name).ShouldBe(["id_token"]);
        context.Properties!.GetTokenValue("id_token").ShouldBe("id.token.value");
    }

    [Fact]
    public async Task Sign_out_names_the_client_to_keycloak()
    {
        var oidc = new OpenIdConnectOptions();
        AdminAuthentication.ConfigureOidc(oidc, Options("mfa"));
        var context = new RedirectContext(new DefaultHttpContext(), Scheme(), oidc, new AuthenticationProperties()) { ProtocolMessage = new OpenIdConnectMessage() };

        await oidc.Events.OnRedirectToIdentityProviderForSignOut(context);

        context.ProtocolMessage.ClientId.ShouldBe("nightware-admin");
    }
}

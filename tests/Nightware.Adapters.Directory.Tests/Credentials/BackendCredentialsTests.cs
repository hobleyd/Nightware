using System.Globalization;
using System.Net;
using System.Text;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using Nightware.Adapters.Directory.Tests.TestSupport;
using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Directory.Tests.Credentials;

/// <summary>Each strategy per backend, through the real DI graph against a WireMock Keycloak.</summary>
public sealed class BackendCredentialsTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-15T09:00:00Z", CultureInfo.InvariantCulture);
    private readonly KeycloakStub _keycloak = new();
    private readonly FakeTimeProvider _time = new(Now);

    public void Dispose() => _keycloak.Dispose();

    private AdapterHost Host(Dictionary<string, string?>? settings = null)
    {
        settings ??= [];
        settings["Directory:MaxRetryAttempts"] = "0";
        AdapterHost host = AdapterHost.Create(_keycloak.Url, settings, _time);
        host.User.BearerToken = Jwt.WithExpiry(Now.AddMinutes(15));
        return host;
    }

    private static async Task<Result<BackendCredential>> For(AdapterHost host, Backend backend)
    {
        using IServiceScope scope = host.Provider.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IBackendCredentials>().ForAsync(backend, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Defaults_exchange_mail_for_stalwart_and_groupware_for_nextcloud()
    {
        _keycloak.GivenExchange("stalwart", "for-stalwart");
        _keycloak.GivenExchange("nextcloud", "for-nextcloud");
        await using AdapterHost host = Host();

        BackendCredential mail = (await For(host, Backend.Mail)).Value;
        BackendCredential groupware = (await For(host, Backend.Groupware)).Value;

        mail.ShouldBe(new BackendCredential("Bearer", "for-stalwart", CredentialStrategy.TokenExchange));
        groupware.ShouldBe(new BackendCredential("Bearer", "for-nextcloud", CredentialStrategy.TokenExchange));
    }

    [Fact]
    public async Task Configured_audience_is_sent()
    {
        _keycloak.GivenExchange("mail-prod", "for-mail");
        await using AdapterHost host = Host(new() { ["BackendCredentials:Mail:Audience"] = "mail-prod" });

        (await For(host, Backend.Mail)).Value.Parameter.ShouldBe("for-mail");
    }

    [Fact]
    public async Task Exchanged_token_is_cached_per_subject_across_requests()
    {
        _keycloak.GivenExchange("stalwart", "for-stalwart");
        await using AdapterHost host = Host();

        await For(host, Backend.Mail);
        await For(host, Backend.Mail);
        _keycloak.ExchangeHits.ShouldBe(1);

        host.User.BearerToken = Jwt.WithExpiry(Now.AddMinutes(15), subject: "another-user");
        await For(host, Backend.Mail);
        _keycloak.ExchangeHits.ShouldBe(2);
    }

    [Fact]
    public async Task Cache_is_separate_per_backend()
    {
        _keycloak.GivenExchange("stalwart", "for-stalwart");
        _keycloak.GivenExchange("nextcloud", "for-nextcloud");
        await using AdapterHost host = Host();

        (await For(host, Backend.Mail)).Value.Parameter.ShouldBe("for-stalwart");
        (await For(host, Backend.Groupware)).Value.Parameter.ShouldBe("for-nextcloud");
        (await For(host, Backend.Mail)).Value.Parameter.ShouldBe("for-stalwart");
        _keycloak.ExchangeHits.ShouldBe(2);
    }

    [Fact]
    public async Task Cached_token_expires_skew_before_the_exchanged_token()
    {
        _keycloak.GivenExchange("stalwart", "for-stalwart", expiresIn: 300);
        await using AdapterHost host = Host();
        host.User.BearerToken = Jwt.WithExpiry(Now.AddHours(1));

        await For(host, Backend.Mail);
        _time.Advance(TimeSpan.FromSeconds(269));
        await For(host, Backend.Mail);
        _keycloak.ExchangeHits.ShouldBe(1);

        _time.Advance(TimeSpan.FromSeconds(1)); // 300 − 30 s skew reached
        await For(host, Backend.Mail);
        _keycloak.ExchangeHits.ShouldBe(2);
    }

    [Fact]
    public async Task Cached_token_never_outlives_the_subject_token()
    {
        _keycloak.GivenExchange("stalwart", "for-stalwart", expiresIn: 900);
        await using AdapterHost host = Host();
        host.User.BearerToken = Jwt.WithExpiry(Now.AddSeconds(120));

        await For(host, Backend.Mail);
        _time.Advance(TimeSpan.FromSeconds(119));
        await For(host, Backend.Mail);
        _keycloak.ExchangeHits.ShouldBe(1);

        _time.Advance(TimeSpan.FromSeconds(1));
        await For(host, Backend.Mail);
        _keycloak.ExchangeHits.ShouldBe(2);
    }

    [Fact]
    public async Task Token_already_inside_the_skew_is_returned_but_not_cached()
    {
        _keycloak.GivenExchange("stalwart", "for-stalwart", expiresIn: 20);
        await using AdapterHost host = Host();

        (await For(host, Backend.Mail)).IsSuccess.ShouldBeTrue();
        (await For(host, Backend.Mail)).IsSuccess.ShouldBeTrue();

        _keycloak.ExchangeHits.ShouldBe(2);
    }

    [Fact]
    public async Task Opaque_subject_token_is_cached_for_the_exchanged_lifetime()
    {
        _keycloak.GivenExchange("stalwart", "for-stalwart", expiresIn: 300);
        await using AdapterHost host = Host();
        host.User.BearerToken = "opaque-reference-token";

        await For(host, Backend.Mail);
        _time.Advance(TimeSpan.FromSeconds(200));
        await For(host, Backend.Mail);

        _keycloak.ExchangeHits.ShouldBe(1);
    }

    [Fact]
    public async Task Cache_can_be_disabled()
    {
        _keycloak.GivenExchange("stalwart", "for-stalwart");
        await using AdapterHost host = Host(new() { ["BackendCredentials:Cache:Enabled"] = "false" });

        await For(host, Backend.Mail);
        await For(host, Backend.Mail);

        _keycloak.ExchangeHits.ShouldBe(2);
    }

    [Fact]
    public async Task Rejected_subject_token_is_backend_auth_failed_and_not_cached()
    {
        _keycloak.GivenExchangeFails(HttpStatusCode.BadRequest, Fixture.Load("error-invalid-token.json"));
        await using AdapterHost host = Host();

        Result<BackendCredential> first = await For(host, Backend.Mail);
        Result<BackendCredential> second = await For(host, Backend.Mail);

        first.Error!.Kind.ShouldBe(ErrorKind.BackendAuthFailed);
        second.Error!.Kind.ShouldBe(ErrorKind.BackendAuthFailed);
        _keycloak.ExchangeHits.ShouldBe(2);
    }

    [Fact]
    public async Task Unavailable_audience_is_backend_unavailable()
    {
        _keycloak.GivenExchangeFails(HttpStatusCode.BadRequest, Fixture.Load("error-audience-not-available.json"));
        await using AdapterHost host = Host();

        (await For(host, Backend.Groupware)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task Pass_through_forwards_the_callers_bearer_token()
    {
        await using AdapterHost host = Host(new() { ["BackendCredentials:Groupware:Strategy"] = "PassThrough" });

        BackendCredential credential = (await For(host, Backend.Groupware)).Value;

        credential.ShouldBe(new BackendCredential("Bearer", host.User.BearerToken, CredentialStrategy.PassThrough));
        _keycloak.Server.LogEntries.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("PassThrough")]
    [InlineData("TokenExchange")]
    public async Task Missing_caller_token_is_unauthorized(string strategy)
    {
        await using AdapterHost host = Host(new() { ["BackendCredentials:Mail:Strategy"] = strategy });
        host.User.BearerToken = string.Empty;

        (await For(host, Backend.Mail)).Error!.Kind.ShouldBe(ErrorKind.Unauthorized);
        _keycloak.Server.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Service_account_basic_acts_as_the_callers_mailbox()
    {
        await using AdapterHost host = Host(new()
        {
            ["BackendCredentials:Mail:Strategy"] = "ServiceAccount",
            ["BackendCredentials:Mail:ServiceAccount:Mode"] = "Basic",
            ["BackendCredentials:Mail:ServiceAccount:Username"] = "admin",
            ["BackendCredentials:Mail:ServiceAccount:Password"] = "s3cr3t:with-colon",
        });

        BackendCredential credential = (await For(host, Backend.Mail)).Value;

        credential.Scheme.ShouldBe("Basic");
        Encoding.UTF8.GetString(Convert.FromBase64String(credential.Parameter)).ShouldBe("admin:s3cr3t:with-colon");
        credential.Strategy.ShouldBe(CredentialStrategy.ServiceAccount);
        credential.ActAs.ShouldBe("alice@nightware.localhost");
        _keycloak.Server.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Service_account_client_credentials_acts_as_the_callers_mailbox()
    {
        _keycloak.GivenServiceAccountToken();
        await using AdapterHost host = Host(new()
        {
            ["BackendCredentials:Groupware:Strategy"] = "ServiceAccount",
            ["BackendCredentials:Groupware:ServiceAccount:Mode"] = "ClientCredentials",
        });

        BackendCredential credential = (await For(host, Backend.Groupware)).Value;

        credential.ShouldBe(new BackendCredential("Bearer", "sa-token-1", CredentialStrategy.ServiceAccount, "alice@nightware.localhost"));
    }

    [Fact]
    public async Task Directory_always_uses_the_service_account_without_impersonation()
    {
        _keycloak.GivenServiceAccountToken();
        await using AdapterHost host = Host(new()
        {
            ["BackendCredentials:Mail:Strategy"] = "PassThrough",
            ["BackendCredentials:Groupware:Strategy"] = "PassThrough",
        });

        BackendCredential first = (await For(host, Backend.Directory)).Value;
        BackendCredential second = (await For(host, Backend.Directory)).Value;

        first.ShouldBe(new BackendCredential("Bearer", "sa-token-1", CredentialStrategy.ServiceAccount));
        second.ShouldBe(first);
        _keycloak.ClientCredentialsHits.ShouldBe(1);
    }

    [Fact]
    public async Task Directory_service_account_failure_is_never_backend_auth_failed()
    {
        _keycloak.GivenServiceAccountTokenFails(HttpStatusCode.Unauthorized, Fixture.Load("error-unauthorized-client.json"));
        await using AdapterHost host = Host();

        (await For(host, Backend.Directory)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task Invalid_configuration_fails_on_first_use()
    {
        await using AdapterHost host = Host(new() { ["BackendCredentials:Mail:Strategy"] = "ServiceAccount" });

        await Should.ThrowAsync<Microsoft.Extensions.Options.OptionsValidationException>(() => For(host, Backend.Mail));
    }
}

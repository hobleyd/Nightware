using Nightware.Adapters.Directory.Core;

namespace Nightware.Adapters.Directory.Tests.Core;

public sealed class DirectoryOptionsTests
{
    private static DirectoryOptions Valid() => new()
    {
        KeycloakBaseUrl = new Uri("https://auth.nightware.localhost/"),
        Realm = "nightware",
        ClientId = "nightware-api",
        ClientSecret = "secret",
    };

    [Fact]
    public void Valid_options_pass() => new DirectoryOptionsValidator().Validate(null, Valid()).Succeeded.ShouldBeTrue();

    [Fact]
    public void Derives_keycloak_endpoints_regardless_of_trailing_slash()
    {
        DirectoryOptions options = Valid();
        options.KeycloakBaseUrl = new Uri("http://keycloak:8080");

        options.TokenEndpoint.ShouldBe(new Uri("http://keycloak:8080/realms/nightware/protocol/openid-connect/token"));
        options.DiscoveryEndpoint.ShouldBe(new Uri("http://keycloak:8080/realms/nightware/.well-known/openid-configuration"));
        options.AdminRealmUrl.ShouldBe(new Uri("http://keycloak:8080/admin/realms/nightware/"));
    }

    [Fact]
    public void Keeps_a_base_path_such_as_legacy_auth()
    {
        DirectoryOptions options = Valid();
        options.KeycloakBaseUrl = new Uri("https://idp.example.com/auth/");

        options.TokenEndpoint.ShouldBe(new Uri("https://idp.example.com/auth/realms/nightware/protocol/openid-connect/token"));
    }

    [Fact]
    public void Missing_base_url_throws_when_an_endpoint_is_needed() =>
        Should.Throw<InvalidOperationException>(() => new DirectoryOptions { Realm = "r" }.TokenEndpoint);

    [Fact]
    public void Reports_every_problem()
    {
        DirectoryOptions options = new()
        {
            KeycloakBaseUrl = new Uri("ftp://x/"),
            Realm = "a/b",
            ClientId = "",
            ClientSecret = "",
            RequestTimeout = TimeSpan.Zero,
            MaxRetryAttempts = 11,
            RetryBaseDelay = TimeSpan.FromSeconds(-1),
            ServiceAccountTokenRefreshSkew = TimeSpan.FromHours(1),
        };

        string message = new DirectoryOptionsValidator().Validate(null, options).FailureMessage!;

        foreach (string field in new[] { "KeycloakBaseUrl", "Realm", "ClientId", "ClientSecret", "RequestTimeout", "MaxRetryAttempts", "RetryBaseDelay", "ServiceAccountTokenRefreshSkew" })
        {
            message.ShouldContain($"Directory:{field}");
        }
    }

    [Fact]
    public void Relative_or_missing_base_url_is_invalid()
    {
        DirectoryOptions options = Valid();
        options.KeycloakBaseUrl = null;

        new DirectoryOptionsValidator().Validate(null, options).Failed.ShouldBeTrue();
    }
}

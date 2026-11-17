using Microsoft.Extensions.Configuration;

using Nightware.Adapters.Dav.Administration;

namespace Nightware.Adapters.Dav.Tests.Administration;

public sealed class NextcloudProvisioningOptionsTests
{
    private static NextcloudProvisioningOptions Valid() => new()
    {
        BaseUrl = new Uri("http://nextcloud/"),
        Username = "nightware-admin",
        Password = "secret",
    };

    private static List<string> Failures(NextcloudProvisioningOptions options) =>
        new NextcloudProvisioningOptionsValidator().Validate(null, options).Failures?.ToList() ?? [];

    [Fact]
    public void Valid_options_pass() => Failures(Valid()).ShouldBeEmpty();

    [Fact]
    public void Defaults_name_the_keycloak_provider_and_user_oidc()
    {
        var options = new NextcloudProvisioningOptions();

        options.OidcProviderId.ShouldBe("keycloak");
        options.EffectiveIdentityProviderBackends.ShouldBe(["user_oidc"]);
        options.MaxRetryAttempts.ShouldBe(2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("nextcloud/relative")]
    [InlineData("ftp://nextcloud/")]
    [InlineData("http://nextcloud/?x=1")]
    [InlineData("http://nextcloud/#frag")]
    [InlineData("http://user:pw@nextcloud/")]
    public void Base_url_must_be_absolute_http_without_extras(string? url)
    {
        var options = Valid();
        options.BaseUrl = url is null ? null : new Uri(url, UriKind.RelativeOrAbsolute);

        Failures(options).ShouldContain(f => f.Contains("BaseUrl", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, "secret", "Username")]
    [InlineData(" ", "secret", "Username")]
    [InlineData("a:b", "secret", "Username")]
    [InlineData("admin", null, "Password")]
    [InlineData("admin", "", "Password")]
    public void Credentials_are_required(string? username, string? password, string key)
    {
        var options = Valid();
        options.Username = username;
        options.Password = password;

        Failures(options).ShouldContain(f => f.Contains(key, StringComparison.Ordinal));
    }

    [Fact]
    public void Failures_never_echo_the_password()
    {
        var options = Valid();
        options.Password = "hunter2";
        options.Username = "";
        options.OidcProviderId = "";

        Failures(options).ShouldAllBe(f => !f.Contains("hunter2", StringComparison.Ordinal));
    }

    [Fact]
    public void Provider_id_is_required()
    {
        var options = Valid();
        options.OidcProviderId = " ";

        Failures(options).ShouldContain(f => f.Contains("OidcProviderId", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("2", 2)]
    [InlineData("17", 17)]
    [InlineData("keycloak", null)]
    [InlineData("0", null)]
    [InlineData("-1", null)]
    [InlineData(" 2", null)]
    public void A_numeric_provider_id_is_used_directly(string value, int? expected)
    {
        var options = Valid();
        options.OidcProviderId = value;

        options.NumericOidcProviderId.ShouldBe(expected);
    }

    [Fact]
    public void Timeouts_and_retries_are_checked()
    {
        var options = Valid();
        options.AttemptTimeout = TimeSpan.FromSeconds(10);
        options.TotalTimeout = TimeSpan.FromSeconds(5);
        options.MaxRetryAttempts = 11;

        var failures = Failures(options);

        failures.ShouldContain(f => f.Contains("TotalTimeout must be at least", StringComparison.Ordinal));
        failures.ShouldContain(f => f.Contains("MaxRetryAttempts", StringComparison.Ordinal));

        options.AttemptTimeout = TimeSpan.Zero;
        options.TotalTimeout = TimeSpan.Zero;
        Failures(options).Count(f => f.Contains("must be positive", StringComparison.Ordinal)).ShouldBe(2);
    }

    [Fact]
    public void Configured_backends_replace_the_default_rather_than_extend_it()
    {
        var options = new NextcloudProvisioningOptions();
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["IdentityProviderBackends:0"] = "LDAP",
            ["IdentityProviderBackends:1"] = " ",
        }).Build().Bind(options);

        options.EffectiveIdentityProviderBackends.ShouldBe(["LDAP"]);
    }

    [Fact]
    public void Resilience_options_carry_the_timeouts()
    {
        var options = Valid();
        options.AttemptTimeout = TimeSpan.FromSeconds(3);
        options.TotalTimeout = TimeSpan.FromSeconds(9);
        options.MaxRetryAttempts = 1;

        var resilience = options.ToResilienceOptions();

        resilience.AttemptTimeout.ShouldBe(TimeSpan.FromSeconds(3));
        resilience.TotalTimeout.ShouldBe(TimeSpan.FromSeconds(9));
        resilience.MaxRetryAttempts.ShouldBe(1);
    }
}

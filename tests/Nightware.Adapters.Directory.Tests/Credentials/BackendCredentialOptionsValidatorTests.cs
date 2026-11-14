using Nightware.Adapters.Directory.Credentials;
using Nightware.Application.Common.Identity;

namespace Nightware.Adapters.Directory.Tests.Credentials;

public sealed class BackendCredentialOptionsValidatorTests
{
    private readonly BackendCredentialOptionsValidator _validator = new();

    [Fact]
    public void Defaults_are_valid_and_exchange_to_the_deploy_audiences()
    {
        BackendCredentialOptions options = new();

        _validator.Validate(null, options).Succeeded.ShouldBeTrue();
        options.Mail.Strategy.ShouldBe(CredentialStrategy.TokenExchange);
        options.Mail.Audience.ShouldBe("stalwart");
        options.Groupware.Audience.ShouldBe("nextcloud");
    }

    [Fact]
    public void Token_exchange_requires_an_audience()
    {
        BackendCredentialOptions options = new() { Mail = new() { Strategy = CredentialStrategy.TokenExchange, Audience = " " } };

        _validator.Validate(null, options).FailureMessage!.ShouldContain("BackendCredentials:Mail:Audience");
    }

    [Theory]
    [InlineData(null, "pw")]
    [InlineData("user:x", "pw")]
    [InlineData("user", null)]
    public void Basic_service_account_requires_username_and_password(string? username, string? password)
    {
        BackendCredentialOptions options = new()
        {
            Groupware = new()
            {
                Strategy = CredentialStrategy.ServiceAccount,
                ServiceAccount = new() { Mode = ServiceAccountMode.Basic, Username = username, Password = password },
            },
        };

        _validator.Validate(null, options).FailureMessage!.ShouldContain("BackendCredentials:Groupware:ServiceAccount");
    }

    [Fact]
    public void Client_credentials_service_account_needs_no_secret_of_its_own()
    {
        BackendCredentialOptions options = new()
        {
            Mail = new() { Strategy = CredentialStrategy.ServiceAccount, ServiceAccount = new() { Mode = ServiceAccountMode.ClientCredentials } },
        };

        _validator.Validate(null, options).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Pass_through_needs_nothing() =>
        _validator.Validate(null, new() { Mail = new() { Strategy = CredentialStrategy.PassThrough, Audience = null } }).Succeeded.ShouldBeTrue();

    [Fact]
    public void Rejects_unknown_enum_values_and_nulls()
    {
        BackendCredentialOptions options = new()
        {
            Mail = new() { Strategy = (CredentialStrategy)99 },
            Groupware = new() { Strategy = CredentialStrategy.ServiceAccount, ServiceAccount = new() { Mode = (ServiceAccountMode)99 } },
            Cache = null!,
        };

        string message = _validator.Validate(null, options).FailureMessage!;
        message.ShouldContain("Mail:Strategy");
        message.ShouldContain("Groupware:ServiceAccount:Mode");
        message.ShouldContain("Cache");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(601)]
    public void Cache_skew_must_be_sane(int seconds) =>
        _validator.Validate(null, new() { Cache = new() { ExpirySkew = TimeSpan.FromSeconds(seconds) } }).FailureMessage!.ShouldContain("ExpirySkew");
}

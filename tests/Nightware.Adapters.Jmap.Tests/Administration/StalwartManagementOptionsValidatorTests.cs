using Nightware.Adapters.Jmap.Administration;

namespace Nightware.Adapters.Jmap.Tests.Administration;

public sealed class StalwartManagementOptionsValidatorTests
{
    private static StalwartManagementOptions Valid() => new()
    {
        ApiUrl = new Uri("http://stalwart:8080/jmap/"),
        Username = "nightware-admin@nightware.localhost",
        Password = "secret",
    };

    private static IEnumerable<string> Failures(StalwartManagementOptions options) =>
        new StalwartManagementOptionsValidator().Validate(null, options).Failures ?? [];

    [Fact]
    public void Accepts_a_password_principal()
    {
        new StalwartManagementOptionsValidator().Validate(null, Valid()).Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Accepts_an_api_key_without_a_username()
    {
        var options = Valid();
        options.Password = null;
        options.Username = null;
        options.ApiKey = "API_key";

        Failures(options).ShouldBeEmpty();
    }

    [Fact]
    public void Requires_exactly_one_credential()
    {
        var both = Valid();
        both.ApiKey = "API_key";
        var neither = Valid();
        neither.Password = null;

        Failures(both).ShouldContain(f => f.Contains("exactly one of Password or ApiKey"));
        Failures(neither).ShouldContain(f => f.Contains("exactly one of Password or ApiKey"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("a:b")]
    public void A_password_needs_a_usable_username(string? username)
    {
        var options = Valid();
        options.Username = username;

        Failures(options).ShouldContain(f => f.Contains("Username"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("/jmap/")]
    [InlineData("ftp://stalwart/jmap/")]
    public void Requires_an_absolute_http_api_url(string? url)
    {
        var options = Valid();
        options.ApiUrl = url is null ? null : new Uri(url, UriKind.RelativeOrAbsolute);

        Failures(options).ShouldContain(f => f.Contains("ApiUrl"));
    }

    [Fact]
    public void Checks_timeouts_retries_and_paging()
    {
        var options = Valid();
        options.AttemptTimeout = TimeSpan.FromMinutes(1);
        options.MaxRetryAttempts = 11;
        options.AccountPageSize = 0;

        Failures(options).Count().ShouldBe(3);
        options.AttemptTimeout = TimeSpan.Zero;
        Failures(options).ShouldContain(f => f.Contains("must be positive"));
    }

    [Fact]
    public void Never_prints_the_secrets()
    {
        var options = Valid();
        options.Password = "pa55word";

        options.ToString().ShouldNotContain("pa55word");
        options.ToString().ShouldContain("Password");
    }
}
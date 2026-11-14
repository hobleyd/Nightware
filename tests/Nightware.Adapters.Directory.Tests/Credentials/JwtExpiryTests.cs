using Nightware.Adapters.Directory.Credentials;
using Nightware.Adapters.Directory.Tests.TestSupport;

namespace Nightware.Adapters.Directory.Tests.Credentials;

public sealed class JwtExpiryTests
{
    [Fact]
    public void Reads_exp_from_the_payload()
    {
        DateTimeOffset exp = DateTimeOffset.FromUnixTimeSeconds(1794691549);

        JwtExpiry.Read(Jwt.WithExpiry(exp)).ShouldBe(exp);
    }

    [Theory]
    [InlineData("opaque")]
    [InlineData("a.b")]
    [InlineData("a..c")]
    [InlineData("a.!!!.c")]
    [InlineData("a.bm90IGpzb24.c")] // "not json"
    public void Returns_null_for_non_jwt(string token) => JwtExpiry.Read(token).ShouldBeNull();

    [Theory]
    [InlineData("""{"sub":"x"}""")]
    [InlineData("""{"exp":"soon"}""")]
    [InlineData("""[1,2]""")]
    [InlineData("""{"exp":1.5}""")]
    public void Returns_null_without_a_numeric_exp(string payload) => JwtExpiry.Read(Jwt.Make(payload)).ShouldBeNull();
}

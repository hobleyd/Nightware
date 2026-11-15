using Nightware.Api.Auth;
using Nightware.Api.Tests.Support;

namespace Nightware.Api.Tests.Auth;

public sealed class AccessTokensTests
{
    [Fact]
    public void Reads_the_expiry_of_a_jwt()
    {
        var expires = DateTime.UtcNow.AddMinutes(7);

        AccessTokens.ExpiryOf(TestHost.Token(expires: expires))!.Value.ShouldBe(new DateTimeOffset(expires, TimeSpan.Zero), TimeSpan.FromSeconds(1));
    }

    [Theory]
    [InlineData("not-a-jwt")]
    [InlineData("")]
    public void Anything_else_has_no_expiry(string token) =>
        AccessTokens.ExpiryOf(token).ShouldBeNull();
}

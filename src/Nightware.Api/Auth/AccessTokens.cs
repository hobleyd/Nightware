using Microsoft.IdentityModel.JsonWebTokens;

namespace Nightware.Api.Auth;

/// <summary>Reading an access token without validating it — for limits only; validation happens in authentication.</summary>
internal static class AccessTokens
{
    /// <summary>The token's <c>exp</c>, when it is a readable JWT that has one.</summary>
    public static DateTimeOffset? ExpiryOf(string token)
    {
        try
        {
            var jwt = new JsonWebToken(token);
            return jwt.ValidTo == DateTime.MinValue ? null : new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

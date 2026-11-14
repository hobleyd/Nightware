using System.Buffers.Text;
using System.Text;

namespace Nightware.Adapters.Directory.Tests.TestSupport;

/// <summary>Unsigned JWT-shaped strings for tests (the adapter only reads <c>exp</c>, never validates).</summary>
internal static class Jwt
{
    public static string WithExpiry(DateTimeOffset expiresAt, string subject = "5d2c6a4e-2f0b-4a8e-9f3c-1b7e2d9a6c11") =>
        Make($$"""{"exp":{{expiresAt.ToUnixTimeSeconds()}},"iat":{{expiresAt.AddMinutes(-15).ToUnixTimeSeconds()}},"iss":"https://auth.nightware.localhost/realms/nightware","aud":"nightware-api","sub":"{{subject}}","typ":"Bearer","azp":"nightmail","email":"alice@nightware.localhost"}""");

    public static string Make(string payloadJson)
    {
        string header = Base64Url.EncodeToString("""{"alg":"RS256","typ":"JWT","kid":"test"}"""u8);
        string payload = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payloadJson));
        string signature = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N")));
        return $"{header}.{payload}.{signature}";
    }
}

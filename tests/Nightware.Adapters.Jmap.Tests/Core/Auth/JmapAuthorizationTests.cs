using System.Text;
using Nightware.Adapters.Jmap.Core.Auth;
using Nightware.Application.Common.Identity;

namespace Nightware.Adapters.Jmap.Tests.Core.Auth;

public sealed class JmapAuthorizationTests
{
    private static string Decode(string base64) => Encoding.UTF8.GetString(Convert.FromBase64String(base64));

    [Fact]
    public void Bearer_tokens_pass_through()
    {
        var header = JmapAuthorization.CreateHeader(new BackendCredential("Bearer", "tok.en", CredentialStrategy.TokenExchange), "%");

        header.Scheme.ShouldBe("Bearer");
        header.Parameter.ShouldBe("tok.en");
    }

    [Fact]
    public void Bearer_ignores_act_as()
    {
        var header = JmapAuthorization.CreateHeader(new BackendCredential("bearer", "t", CredentialStrategy.PassThrough, "bob@x"), "%");

        header.Parameter.ShouldBe("t");
    }

    [Fact]
    public void Basic_accepts_raw_user_password()
    {
        var header = JmapAuthorization.CreateHeader(new BackendCredential("Basic", "svc:pa:ss", CredentialStrategy.ServiceAccount), "%");

        header.Scheme.ShouldBe("Basic");
        Decode(header.Parameter!).ShouldBe("svc:pa:ss");
    }

    [Fact]
    public void Basic_accepts_base64_user_password()
    {
        var header = JmapAuthorization.CreateHeader(new BackendCredential("Basic", "dXNlcjpwYTpzcw==", CredentialStrategy.ServiceAccount), "%");

        Decode(header.Parameter!).ShouldBe("user:pa:ss");
    }

    [Fact]
    public void Basic_with_act_as_uses_stalwart_master_user_syntax()
    {
        var credential = new BackendCredential("Basic", "admin:secret", CredentialStrategy.ServiceAccount, "alice@nightware.localhost");

        Decode(JmapAuthorization.CreateHeader(credential, "%").Parameter!).ShouldBe("alice@nightware.localhost%admin:secret");
        Decode(JmapAuthorization.CreateHeader(credential, "*").Parameter!).ShouldBe("alice@nightware.localhost*admin:secret");
    }

    [Theory]
    [InlineData("nocolon-and-not-base64!")]
    [InlineData(":password")]
    [InlineData("bm9jb2xvbg==")] // "nocolon"
    public void Malformed_basic_credentials_are_bugs(string parameter)
    {
        Should.Throw<InvalidOperationException>(() =>
            JmapAuthorization.CreateHeader(new BackendCredential("Basic", parameter, CredentialStrategy.ServiceAccount), "%"));
    }

    [Fact]
    public void Other_schemes_pass_through()
    {
        var header = JmapAuthorization.CreateHeader(new BackendCredential("Custom", "v", CredentialStrategy.PassThrough), "%");

        header.Scheme.ShouldBe("Custom");
        header.Parameter.ShouldBe("v");
    }

    [Fact]
    public void Identity_key_distinguishes_credentials_and_hides_the_secret()
    {
        var a = JmapAuthorization.IdentityKey(new BackendCredential("Bearer", "secret-a", CredentialStrategy.PassThrough), "%");
        var b = JmapAuthorization.IdentityKey(new BackendCredential("Bearer", "secret-b", CredentialStrategy.PassThrough), "%");
        var asAlice = JmapAuthorization.IdentityKey(new BackendCredential("Basic", "m:p", CredentialStrategy.ServiceAccount, "alice@x"), "%");
        var asBob = JmapAuthorization.IdentityKey(new BackendCredential("Basic", "m:p", CredentialStrategy.ServiceAccount, "bob@x"), "%");

        a.ShouldNotBe(b);
        asAlice.ShouldNotBe(asBob);
        a.ShouldNotContain("secret");
        a.ShouldBe(JmapAuthorization.IdentityKey(new BackendCredential("Bearer", "secret-a", CredentialStrategy.TokenExchange), "%"));
    }
}

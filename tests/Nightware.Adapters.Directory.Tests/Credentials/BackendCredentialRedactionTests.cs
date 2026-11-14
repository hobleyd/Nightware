using Nightware.Application.Common.Identity;

namespace Nightware.Adapters.Directory.Tests.Credentials;

public sealed class BackendCredentialRedactionTests
{
    [Fact]
    public void Backend_credential_ToString_never_shows_the_parameter()
    {
        BackendCredential credential = new("Bearer", "eyJhbGciOi.secret.sig", CredentialStrategy.TokenExchange, "alice@nightware.localhost");

        credential.ToString().ShouldNotContain("secret");
        credential.ToString().ShouldContain("TokenExchange");
    }
}

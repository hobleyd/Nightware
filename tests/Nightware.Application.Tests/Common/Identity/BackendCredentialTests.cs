using Nightware.Application.Common.Identity;
using Nightware.Application.Mail;

namespace Nightware.Application.Tests.Common.Identity;

public sealed class BackendCredentialTests
{
    [Fact]
    public void ToString_never_reveals_the_credential()
    {
        var credential = new BackendCredential("Bearer", "secret-token", CredentialStrategy.TokenExchange);

        credential.ToString().ShouldBe("Bearer *** (TokenExchange)");
        credential.ToString().ShouldNotContain("secret");
    }

    [Fact]
    public void ToString_names_the_impersonated_account()
    {
        new BackendCredential("Basic", "x", CredentialStrategy.ServiceAccount, "alice").ToString()
            .ShouldBe("Basic *** (ServiceAccount as alice)");
    }

    [Fact]
    public void Message_fetch_and_delta_request_defaults()
    {
        MessageFetch.Default.Selects("body").ShouldBeTrue();
        new MessageFetch(new HashSet<string> { "id" }).Selects("body").ShouldBeFalse();
        DeltaRequest.Default.PageSize.ShouldBe(DeltaRequest.DefaultPageSize);
        DeltaRequest.Default.Select.ShouldBeNull();
    }
}

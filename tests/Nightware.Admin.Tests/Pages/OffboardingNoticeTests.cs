using Nightware.Admin.Pages.Users;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;

namespace Nightware.Admin.Tests.Pages;

public sealed class OffboardingNoticeTests
{
    [Fact]
    public void Names_the_user_and_what_each_system_now_says()
    {
        var user = new UserDetail(
            new ManagedUser(new IdentityAccount("u-eve", "eve", "eve@acme.example", false), "e-acme", false, false),
            [
                new BackendLink(BackendKind.Identity, LinkState.Disabled, "Sign-in disabled.", "u-eve"),
                new BackendLink(BackendKind.Mail, LinkState.Disabled, "Mailbox sign-in disabled.", "m-eve"),
                new BackendLink(BackendKind.Groupware, LinkState.Missing, null, null),
            ]);

        string notice = OffboardingNotice.For(user, TimeSpan.FromMinutes(15));

        notice.ShouldStartWith("eve is offboarded.");
        notice.ShouldContain("Mailbox sign-in disabled.");
        notice.ShouldContain("Groupware: Missing.");
        notice.ShouldNotContain("Sign-in disabled. Mailbox");
        notice.ShouldContain("within 15 minutes");
    }
}

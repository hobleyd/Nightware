using Nightware.Admin.Pages;
using Nightware.Application.Administration.Users;
using Nightware.Domain.Administration;

namespace Nightware.Admin.Tests.Pages;

public sealed class SystemsTests
{
    [Theory]
    [InlineData(BackendKind.Identity, "Sign-in (Keycloak)", "Sign-in")]
    [InlineData(BackendKind.Mail, "Mail (Stalwart)", "Mail")]
    [InlineData(BackendKind.Groupware, "Files & calendar (Nextcloud)", "Files & calendar")]
    public void Every_system_has_one_name_leading_with_what_it_does(BackendKind backend, string name, string shortName)
    {
        Systems.Name(backend).ShouldBe(name);
        Systems.ShortName(backend).ShouldBe(shortName);
    }

    [Fact]
    public void A_domain_repair_says_whether_the_dkim_keys_arrived()
    {
        MailDomain acme = MailDomain.TryParse("acme.example", out MailDomain? d) ? d : throw new InvalidOperationException();
        BackendLink served = new(BackendKind.Mail, LinkState.Linked, "Served.", "d1");

        Systems.DomainRepairNotice(new DomainStatus(acme, served, true)).ShouldBe("acme.example is served by the mail server, with DKIM keys.");
        Systems.DomainRepairNotice(new DomainStatus(acme, served, false)).ShouldBe("acme.example is served by the mail server, but still has no DKIM keys.");
        Systems.DomainRepairNotice(new DomainStatus(acme, new BackendLink(BackendKind.Mail, LinkState.Error, "Refused."), false))
            .ShouldBe("acme.example is still not served by the mail server (Refused.).");
    }

    [Fact]
    public void States_and_statuses_read_as_words()
    {
        Systems.StateLabel(LinkState.Error).ShouldBe("Unreachable");
        Systems.StateLabel(LinkState.Pending).ShouldBe("Pending first sign-in");
        Systems.StateLabel(LinkState.Linked).ShouldBe("Linked");
        Systems.StatusLabel(UserStatus.OffboardingIncomplete).ShouldBe("Offboarding incomplete");
        Systems.StatusClass(UserStatus.OffboardingIncomplete).ShouldBe("error");
        Systems.StatusClass(UserStatus.Active).ShouldBe("linked");
    }
}

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
        Systems.StateLabel(LinkState.Error).ShouldBe("Failed");
        Systems.StateLabel(LinkState.Pending).ShouldBe("Pending first sign-in");
        Systems.StateLabel(LinkState.Linked).ShouldBe("Linked");
        Systems.StatusLabel(UserStatus.OffboardingIncomplete).ShouldBe("Offboarding incomplete");
        Systems.StatusClass(UserStatus.OffboardingIncomplete).ShouldBe("error");
        Systems.StatusClass(UserStatus.Active).ShouldBe("linked");
    }

    [Fact]
    public void An_offboarded_users_missing_account_is_harmless_absence_and_nothing_else_changes()
    {
        var missing = new BackendLink(BackendKind.Mail, LinkState.Missing);
        var failed = new BackendLink(BackendKind.Mail, LinkState.Error);

        Systems.LinkLabel(missing, offboarded: true).ShouldBe("None");
        Systems.LinkClass(missing, offboarded: true).ShouldBe("disabled");
        Systems.LinkLabel(missing, offboarded: false).ShouldBe("Missing");
        Systems.LinkClass(missing, offboarded: false).ShouldBe("missing");
        Systems.LinkLabel(failed, offboarded: true).ShouldBe("Failed");
        Systems.LinkClass(failed, offboarded: true).ShouldBe("error");
    }

    [Fact]
    public void A_history_entry_reads_as_what_who_and_what_each_system_said()
    {
        var at = new DateTimeOffset(2026, 11, 18, 19, 12, 0, TimeSpan.FromHours(10));
        var offboarded = new AccessEvent(at, AccessEventKind.Offboarded, "ops", [new SystemState(BackendKind.Identity, LinkState.Disabled), new SystemState(BackendKind.Mail, LinkState.Error), new SystemState(BackendKind.Groupware, LinkState.Missing)]);

        Systems.When(at).ShouldBe("18 Nov 2026, 09:12 UTC");
        Systems.HistoryLine(offboarded).ShouldBe("Offboarded by ops: Sign-in shut, Mail failed, Files & calendar none");
        Systems.HistoryLine(new AccessEvent(at, AccessEventKind.Reinstated, "ops", [])).ShouldBe("Reinstated by ops");
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(44, 1)]
    [InlineData(130, 3)]
    [InlineData(2000, 6)]
    public void A_record_value_gets_rows_to_show_it_whole_up_to_six(int length, int rows) =>
        Systems.ValueRows(new string('x', length)).ShouldBe(rows);
}

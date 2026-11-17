using Nightware.Application.Administration;
using Nightware.Application.Administration.Admins;
using Nightware.Application.Administration.Entities;
using Nightware.Application.Administration.Status;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Administration;

public sealed class EntityAndAdminTests
{
    private readonly AdminTestBed _bed = new();

    private static CancellationToken Ct => AdminTestBed.Ct;

    [Fact]
    public async Task Entity_admins_see_only_their_entities()
    {
        _bed.Entities.ListAsync(Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<Entity>>.Success([AdminTestBed.OtherEntity, AdminTestBed.AcmeEntity]));
        var list = new ListEntities(_bed.Actor, _bed.Entities);

        (await list.ExecuteAsync(Ct)).Value.Select(e => e.Alias).ShouldBe(["acme", "other"]);
        _bed.ActAsEntityAdmin();
        (await list.ExecuteAsync(Ct)).Value.Select(e => e.Alias).ShouldBe(["acme"]);
        _bed.ActAsNobody();
        (await list.ExecuteAsync(Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task GetEntity_reports_each_domain_on_the_mail_server()
    {
        _bed.MailDomains.FindAsync(AdminTestBed.Domain("acme.example"), Arg.Any<CancellationToken>()).Returns(new MailDomainInfo("d1", AdminTestBed.Domain("acme.example"), true));

        EntityDetail detail = (await new GetEntity(_bed.Actor, _bed.Entities, _bed.DomainProvisioning()).ExecuteAsync(AdminTestBed.Acme, Ct)).Value;

        detail.Domains.Single().Mail.State.ShouldBe(LinkState.Linked);
        detail.Domains.Single().DkimPresent.ShouldBeTrue();
        _bed.ActAsEntityAdmin();
        (await new GetEntity(_bed.Actor, _bed.Entities, _bed.DomainProvisioning()).ExecuteAsync(AdminTestBed.Other, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task Creating_an_entity_creates_the_mail_domain_too()
    {
        _bed.Entities.CreateAsync(Arg.Any<NewEntity>(), Arg.Any<CancellationToken>()).Returns(c => Result<Entity>.Success(new Entity("e-new", c.Arg<NewEntity>().Alias, c.Arg<NewEntity>().Name, [c.Arg<NewEntity>().FirstDomain])));

        EntityDetail detail = (await new CreateEntity(_bed.Actor, _bed.Entities, _bed.DomainProvisioning(), _bed.Audit).ExecuteAsync(" Beta Pty ", "Beta", "Beta.Example", Ct)).Value;

        detail.Entity.Alias.ShouldBe("beta");
        detail.Domains.Single().Mail.State.ShouldBe(LinkState.Linked);
        await _bed.MailDomains.Received(1).CreateAsync(AdminTestBed.Domain("beta.example"), "Beta Pty (beta)", Arg.Any<CancellationToken>());
        _bed.Audit.Single("entity.created").Details["mail"].ShouldBe("Linked");
    }

    [Fact]
    public async Task A_mail_server_failure_leaves_the_entity_with_a_domain_to_repair()
    {
        _bed.Entities.CreateAsync(Arg.Any<NewEntity>(), Arg.Any<CancellationToken>()).Returns(new Entity("e-new", "beta", "Beta", [AdminTestBed.Domain("beta.example")]));
        _bed.MailDomains.CreateAsync(default!, default!, Ct).ReturnsForAnyArgs(Error.BackendUnavailable("down"));

        EntityDetail detail = (await new CreateEntity(_bed.Actor, _bed.Entities, _bed.DomainProvisioning(), _bed.Audit).ExecuteAsync("Beta", "beta", "beta.example", Ct)).Value;

        detail.Domains.Single().Mail.State.ShouldBe(LinkState.Error);
    }

    [Theory]
    [InlineData("", "beta", "beta.example", "name")]
    [InlineData("Beta", "b", "beta.example", "alias")]
    [InlineData("Beta", "-beta", "beta.example", "alias")]
    [InlineData("Beta", "be ta", "beta.example", "alias")]
    [InlineData("Beta", "beta", "not a domain", "domain")]
    public async Task Entity_input_is_validated(string name, string alias, string domain, string target) =>
        (await new CreateEntity(_bed.Actor, _bed.Entities, _bed.DomainProvisioning(), _bed.Audit).ExecuteAsync(name, alias, domain, Ct)).Error!.Target.ShouldBe(target);

    [Fact]
    public async Task Only_global_admins_create_entities_and_change_domains()
    {
        _bed.ActAsEntityAdmin();

        (await new CreateEntity(_bed.Actor, _bed.Entities, _bed.DomainProvisioning(), _bed.Audit).ExecuteAsync("B", "beta", "beta.example", Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        (await new AddEntityDomain(_bed.Actor, _bed.Entities, _bed.DomainProvisioning(), _bed.Audit).ExecuteAsync(AdminTestBed.Acme, "acme.org", Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        (await new RemoveEntityDomain(_bed.Actor, _bed.Entities, _bed.Audit).ExecuteAsync(AdminTestBed.Acme, "acme.example", Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        await _bed.Entities.DidNotReceiveWithAnyArgs().CreateAsync(default!, Ct);
    }

    [Fact]
    public async Task Adding_a_domain_the_mail_server_has_is_a_link_not_a_create()
    {
        _bed.Entities.AddDomainAsync(AdminTestBed.Acme, Arg.Any<MailDomain>(), Arg.Any<CancellationToken>()).Returns(AdminTestBed.AcmeEntity);
        _bed.MailDomains.FindAsync(AdminTestBed.Domain("acme.org"), Arg.Any<CancellationToken>()).Returns(new MailDomainInfo("d2", AdminTestBed.Domain("acme.org"), false));

        DomainStatus status = (await new AddEntityDomain(_bed.Actor, _bed.Entities, _bed.DomainProvisioning(), _bed.Audit).ExecuteAsync(AdminTestBed.Acme, "acme.org", Ct)).Value;

        status.Mail.State.ShouldBe(LinkState.Linked);
        await _bed.MailDomains.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task Repairing_a_served_domain_without_dkim_keys_reloads_the_mail_server()
    {
        MailDomain org = AdminTestBed.Domain("acme.org");
        _bed.Entities.AddDomainAsync(AdminTestBed.Acme, Arg.Any<MailDomain>(), Arg.Any<CancellationToken>()).Returns(AdminTestBed.AcmeEntity);
        _bed.MailDomains.FindAsync(org, Arg.Any<CancellationToken>()).Returns(new MailDomainInfo("d2", org, false), new MailDomainInfo("d2", org, true));

        DomainStatus status = (await new AddEntityDomain(_bed.Actor, _bed.Entities, _bed.DomainProvisioning(), _bed.Audit).ExecuteAsync(AdminTestBed.Acme, "acme.org", Ct)).Value;

        status.Mail.State.ShouldBe(LinkState.Linked);
        status.DkimPresent.ShouldBeTrue();
        await _bed.MailDomains.Received(1).ReloadSettingsAsync(Arg.Any<CancellationToken>());
        await _bed.MailDomains.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task A_refused_reload_leaves_the_domain_in_error_with_the_reason()
    {
        MailDomain org = AdminTestBed.Domain("acme.org");
        _bed.Entities.AddDomainAsync(AdminTestBed.Acme, Arg.Any<MailDomain>(), Arg.Any<CancellationToken>()).Returns(AdminTestBed.AcmeEntity);
        _bed.MailDomains.FindAsync(org, Arg.Any<CancellationToken>()).Returns(new MailDomainInfo("d2", org, false));
        _bed.MailDomains.ReloadSettingsAsync(Arg.Any<CancellationToken>()).Returns(Error.Forbidden("Insufficient permissions"));

        DomainStatus status = (await new AddEntityDomain(_bed.Actor, _bed.Entities, _bed.DomainProvisioning(), _bed.Audit).ExecuteAsync(AdminTestBed.Acme, "acme.org", Ct)).Value;

        status.Mail.State.ShouldBe(LinkState.Error);
        status.Mail.Detail!.ShouldContain("without DKIM keys");
        status.Mail.Detail!.ShouldContain("Insufficient permissions");
    }

    [Fact]
    public async Task A_domain_with_dkim_keys_is_not_reloaded()
    {
        MailDomain org = AdminTestBed.Domain("acme.org");
        _bed.Entities.AddDomainAsync(AdminTestBed.Acme, Arg.Any<MailDomain>(), Arg.Any<CancellationToken>()).Returns(AdminTestBed.AcmeEntity);
        _bed.MailDomains.FindAsync(org, Arg.Any<CancellationToken>()).Returns(new MailDomainInfo("d2", org, true));

        await new AddEntityDomain(_bed.Actor, _bed.Entities, _bed.DomainProvisioning(), _bed.Audit).ExecuteAsync(AdminTestBed.Acme, "acme.org", Ct);

        await _bed.MailDomains.DidNotReceiveWithAnyArgs().ReloadSettingsAsync(Ct);
    }

    [Fact]
    public async Task A_domain_in_use_cannot_be_removed()
    {
        _bed.Entities.ListMembersAsync(AdminTestBed.Acme, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<IdentityAccount>>.Success(
            [new IdentityAccount("u", "c", "c@acme.org", true), new IdentityAccount("v", "d", "d@acme.example", true) { Aliases = ["x@acme.example"] }]));
        var remove = new RemoveEntityDomain(_bed.Actor, _bed.Entities, _bed.Audit);
        _bed.Entities.RemoveDomainAsync(default!, default!, Ct).ReturnsForAnyArgs(AdminTestBed.AcmeEntity);

        (await remove.ExecuteAsync(AdminTestBed.Acme, "acme.example", Ct)).Error!.Kind.ShouldBe(ErrorKind.Conflict);
        (await remove.ExecuteAsync(AdminTestBed.Acme, "unused.example", Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Dns_records_come_from_the_mail_server_for_the_entitys_own_domains()
    {
        DnsRecord mx = new("acme.example.", "MX", 3600, "10 mail.acme.example.");
        _bed.MailDomains.GetDnsRecordsAsync(AdminTestBed.Domain("acme.example"), Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<DnsRecord>>.Success([mx]));
        var dns = new GetEntityDnsRecords(_bed.Actor, _bed.Entities, _bed.MailDomains, _bed.PublicAddresses());

        DomainDns acme = (await dns.ExecuteAsync(AdminTestBed.Acme, "acme.example", Ct)).Value;
        acme.Records[0].ShouldBe(mx);
        acme.Policy.ShouldBe(DomainMailPolicy.MailServerDefault);
        (await dns.ExecuteAsync(AdminTestBed.Acme, "other.example", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Dns_records_carry_the_entitys_own_spf_and_dmarc_in_place_of_the_mail_servers()
    {
        MailDomain acme = AdminTestBed.Domain("acme.example");
        DmarcRecord dmarc = DmarcRecord.Parse("v=DMARC1; p=none").Value;
        _bed.Entities.GetAsync(AdminTestBed.Acme, Arg.Any<CancellationToken>()).Returns(AdminTestBed.AcmeEntity with
        {
            MailPolicies = new Dictionary<MailDomain, DomainMailPolicy> { [acme] = new(null, dmarc) },
        });
        _bed.MailDomains.GetDnsRecordsAsync(acme, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<DnsRecord>>.Success(
            [new("acme.example.", "TXT", 3600, "v=spf1 mx -all"), new("_dmarc.acme.example.", "TXT", 3600, "v=DMARC1; p=reject")]));

        DomainDns dns = (await new GetEntityDnsRecords(_bed.Actor, _bed.Entities, _bed.MailDomains, _bed.PublicAddresses()).ExecuteAsync(AdminTestBed.Acme, "acme.example", Ct)).Value;

        dns.Records.Select(r => r.Value).ShouldBe(["v=spf1 mx -all", "v=DMARC1; p=none"]);
        dns.Policy.Dmarc.ShouldBe(dmarc);
    }

    [Fact]
    public async Task A_global_admin_sets_and_clears_a_domains_spf_and_dmarc()
    {
        MailDomain acme = AdminTestBed.Domain("acme.example");
        _bed.Entities.SetMailPolicyAsync(default!, default!, default!, Ct).ReturnsForAnyArgs(AdminTestBed.AcmeEntity);
        var set = new SetDomainMailPolicy(_bed.Actor, _bed.Entities, _bed.Audit);

        DomainMailPolicy policy = (await set.ExecuteAsync(AdminTestBed.Acme, "Acme.Example", " v=spf1 mx -all ", "v=DMARC1; p=quarantine", Ct)).Value;
        DomainMailPolicy cleared = (await set.ExecuteAsync(AdminTestBed.Acme, "acme.example", "", "  ", Ct)).Value;

        policy.Spf!.Value.ShouldBe("v=spf1 mx -all");
        policy.Dmarc!.Policy.ShouldBe("quarantine");
        cleared.IsDefault.ShouldBeTrue();
        await _bed.Entities.Received(1).SetMailPolicyAsync(AdminTestBed.Acme, acme, policy, Arg.Any<CancellationToken>());
        await _bed.Entities.Received(1).SetMailPolicyAsync(AdminTestBed.Acme, acme, DomainMailPolicy.MailServerDefault, Arg.Any<CancellationToken>());
        AuditEvent first = _bed.Audit.Events.First(e => e.Action == "entity.domain.mail-policy");
        first.Succeeded.ShouldBeTrue();
        first.Details["dmarc"].ShouldBe("v=DMARC1; p=quarantine");
    }

    [Fact]
    public async Task A_malformed_record_or_an_entity_admin_changes_nothing()
    {
        var set = new SetDomainMailPolicy(_bed.Actor, _bed.Entities, _bed.Audit);

        (await set.ExecuteAsync(AdminTestBed.Acme, "acme.example", "mx -all", null, Ct)).Error!.Target.ShouldBe("spf");
        (await set.ExecuteAsync(AdminTestBed.Acme, "acme.example", null, "v=DMARC1", Ct)).Error!.Target.ShouldBe("dmarc");
        (await set.ExecuteAsync(AdminTestBed.Acme, "not a domain", null, null, Ct)).Error!.Target.ShouldBe("domain");
        _bed.ActAsEntityAdmin();
        (await set.ExecuteAsync(AdminTestBed.Acme, "acme.example", "v=spf1 -all", null, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);

        await _bed.Entities.DidNotReceiveWithAnyArgs().SetMailPolicyAsync(default!, default!, default!, Ct);
        _bed.Audit.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_refused_policy_write_is_audited_as_failed()
    {
        _bed.Entities.SetMailPolicyAsync(default!, default!, default!, Ct).ReturnsForAnyArgs(Error.NotFound("The entity has no domain acme.org.", "domain"));

        Result<DomainMailPolicy> result = await new SetDomainMailPolicy(_bed.Actor, _bed.Entities, _bed.Audit).ExecuteAsync(AdminTestBed.Acme, "acme.org", "v=spf1 -all", null, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _bed.Audit.Single("entity.domain.mail-policy").Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task An_entity_admin_grants_admin_to_a_member_of_their_entity_who_then_needs_a_second_factor()
    {
        _bed.ActAsEntityAdmin();
        _bed.AddUser("u-dan", "dan", "dan@acme.example");
        var grant = new GrantEntityAdmin(_bed.Actor, _bed.Entities, _bed.Identities, _bed.UserAccess(), _bed.Audit);

        (await grant.ExecuteAsync(AdminTestBed.Acme, "u-dan", Ct)).IsSuccess.ShouldBeTrue();

        await _bed.Entities.Received(1).GrantAdminAsync(AdminTestBed.Acme, "u-dan", Arg.Any<CancellationToken>());
        await _bed.Identities.Received(1).AddRequiredActionAsync("u-dan", RequiredAction.ConfigureTotp, Arg.Any<CancellationToken>());
        _bed.Audit.Single("entity.admin.granted").TargetUserId.ShouldBe("u-dan");
    }

    [Fact]
    public async Task Entity_admin_grants_are_refused_for_self_other_entities_and_non_members()
    {
        _bed.ActAsEntityAdmin(userId: "u-eadmin");
        _bed.AddUser("u-eadmin", "eadmin", "eadmin@acme.example");
        _bed.AddUser("u-out", "out", "out@other.example", entities: [AdminTestBed.Other]);
        var grant = new GrantEntityAdmin(_bed.Actor, _bed.Entities, _bed.Identities, _bed.UserAccess(), _bed.Audit);

        (await grant.ExecuteAsync(AdminTestBed.Acme, "u-eadmin", Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        (await grant.ExecuteAsync(AdminTestBed.Other, "u-out", Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        (await grant.ExecuteAsync(AdminTestBed.Acme, "u-out", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);

        _bed.ActAsGlobal();
        (await grant.ExecuteAsync(AdminTestBed.Acme, "u-out", Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        await _bed.Entities.DidNotReceiveWithAnyArgs().GrantAdminAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task Revoking_entity_admin_is_refused_on_self()
    {
        _bed.ActAsEntityAdmin(userId: "u-eadmin");
        _bed.AddUser("u-dan", "dan", "dan@acme.example", scope: new AdminScope(false, new HashSet<string> { AdminTestBed.Acme }));
        var revoke = new RevokeEntityAdmin(_bed.Actor, _bed.Entities, _bed.UserAccess(), _bed.Audit);

        (await revoke.ExecuteAsync(AdminTestBed.Acme, "u-eadmin", Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        (await revoke.ExecuteAsync(AdminTestBed.Acme, "u-dan", Ct)).IsSuccess.ShouldBeTrue();
        await _bed.Entities.Received(1).RevokeAdminAsync(AdminTestBed.Acme, "u-dan", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListEntityAdmins_is_scoped()
    {
        _bed.Entities.ListAdminsAsync(AdminTestBed.Acme, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<IdentityAccount>>.Success([]));
        _bed.ActAsEntityAdmin();

        (await new ListEntityAdmins(_bed.Actor, _bed.Entities).ExecuteAsync(AdminTestBed.Acme, Ct)).IsSuccess.ShouldBeTrue();
        (await new ListEntityAdmins(_bed.Actor, _bed.Entities).ExecuteAsync(AdminTestBed.Other, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task Without_an_spf_record_from_the_server_the_hardened_one_names_the_servers_public_address()
    {
        _bed.Options.PublicHost = "mail.operator.example";
        _bed.PublicDns.ResolveAsync("mail.operator.example", Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<System.Net.IPAddress>>.Success([System.Net.IPAddress.Parse("81.2.69.160")]));
        _bed.MailDomains.GetDnsRecordsAsync(default!, Ct).ReturnsForAnyArgs(Result<IReadOnlyList<DnsRecord>>.Success([new DnsRecord("acme.example", "MX", 3600, "10 mail.acme.example")]));
        var dns = new GetEntityDnsRecords(_bed.Actor, _bed.Entities, _bed.MailDomains, _bed.PublicAddresses());

        DomainDns found = (await dns.ExecuteAsync(AdminTestBed.Acme, "acme.example", Ct)).Value;

        DomainMailPolicy.SpfIn(AdminTestBed.Domain("acme.example"), found.Records).ShouldBe("v=spf1 ip4:81.2.69.160 -all");
        DomainMailPolicy.DmarcIn(AdminTestBed.Domain("acme.example"), found.Records).ShouldBe("v=DMARC1; p=reject; sp=reject; adkim=s; aspf=s; rua=mailto:postmaster@acme.example");
    }

    [Fact]
    public async Task When_the_server_publishes_spf_no_address_is_looked_up()
    {
        _bed.Options.PublicHost = "mail.operator.example";
        _bed.MailDomains.GetDnsRecordsAsync(default!, Ct).ReturnsForAnyArgs(Result<IReadOnlyList<DnsRecord>>.Success(
            [new DnsRecord("acme.example", "MX", 3600, "10 mail.acme.example"), new DnsRecord("acme.example", "TXT", 3600, "v=spf1 mx -all")]));

        (await new GetEntityDnsRecords(_bed.Actor, _bed.Entities, _bed.MailDomains, _bed.PublicAddresses()).ExecuteAsync(AdminTestBed.Acme, "acme.example", Ct)).IsSuccess.ShouldBeTrue();

        await _bed.PublicDns.DidNotReceiveWithAnyArgs().ResolveAsync(default!, Ct);
    }

    [Fact]
    public async Task Global_admin_role_is_global_only_never_self_and_requires_a_second_factor()
    {
        _bed.AddUser("u-dan", "dan", "dan@acme.example");
        var set = new SetGlobalAdmin(_bed.Actor, _bed.Identities, _bed.Audit, _bed.Options, _bed.Time);

        (await set.ExecuteAsync("u-dan", true, Ct)).IsSuccess.ShouldBeTrue();
        await _bed.Identities.Received(1).AddRequiredActionAsync("u-dan", RequiredAction.ConfigureTotp, Arg.Any<CancellationToken>());
        (await set.ExecuteAsync("u-dan", false, Ct)).IsSuccess.ShouldBeTrue();
        _bed.Audit.Events.Select(e => e.Action).ShouldBe(["global.admin.granted", "global.admin.revoked"]);

        (await set.ExecuteAsync("u-admin", false, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        _bed.ActAsEntityAdmin();
        (await set.ExecuteAsync("u-dan", true, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        (await new ListGlobalAdmins(_bed.Actor, _bed.Identities).ExecuteAsync(Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task An_offboarded_user_cannot_be_granted_global_admin_but_can_lose_it()
    {
        _bed.AddUser("u-eve", "eve", "eve@acme.example", enabled: false);
        var set = new SetGlobalAdmin(_bed.Actor, _bed.Identities, _bed.Audit, _bed.Options, _bed.Time);

        Error refused = (await set.ExecuteAsync("u-eve", true, Ct)).Error!;
        refused.Kind.ShouldBe(ErrorKind.Conflict);
        refused.Message.ShouldContain("reinstate them");
        await _bed.Identities.DidNotReceiveWithAnyArgs().SetGlobalAdminAsync(default!, default, Ct);

        (await set.ExecuteAsync("u-eve", false, Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Granting_global_admin_needs_a_fresh_sign_in_but_revoking_does_not()
    {
        _bed.AddUser("u-dan", "dan", "dan@acme.example");
        var set = new SetGlobalAdmin(_bed.Actor, _bed.Identities, _bed.Audit, _bed.Options, _bed.Time);
        _bed.SignedInAgo(TimeSpan.FromMinutes(10));

        (await set.ExecuteAsync("u-dan", true, Ct)).Error!.Code.ShouldBe("reauthenticationRequired");
        await _bed.Identities.DidNotReceiveWithAnyArgs().SetGlobalAdminAsync(default!, default, Ct);

        (await set.ExecuteAsync("u-dan", false, Ct)).IsSuccess.ShouldBeTrue();
        await _bed.Identities.Received(1).SetGlobalAdminAsync("u-dan", false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task System_status_lists_every_backend()
    {
        IBackendStatus[] backends =
        [
            Status(new BackendHealth(BackendKind.Groupware, true)),
            Status(new BackendHealth(BackendKind.Identity, false, "token refused")),
        ];

        IReadOnlyList<BackendHealth> health = (await new GetSystemStatus(_bed.Actor, backends).ExecuteAsync(Ct)).Value;

        health.Select(h => h.Backend).ShouldBe([BackendKind.Identity, BackendKind.Groupware]);
        _bed.ActAsNobody();
        (await new GetSystemStatus(_bed.Actor, backends).ExecuteAsync(Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    private static IBackendStatus Status(BackendHealth health)
    {
        IBackendStatus status = Substitute.For<IBackendStatus>();
        status.CheckAsync(Arg.Any<CancellationToken>()).Returns(health);
        return status;
    }
}

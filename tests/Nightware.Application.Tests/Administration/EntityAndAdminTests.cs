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
        var dns = new GetEntityDnsRecords(_bed.Actor, _bed.Entities, _bed.MailDomains);

        (await dns.ExecuteAsync(AdminTestBed.Acme, "acme.example", Ct)).Value.ShouldBe([mx]);
        (await dns.ExecuteAsync(AdminTestBed.Acme, "other.example", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
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
    public async Task Global_admin_role_is_global_only_never_self_and_requires_a_second_factor()
    {
        _bed.AddUser("u-dan", "dan", "dan@acme.example");
        var set = new SetGlobalAdmin(_bed.Actor, _bed.Identities, _bed.Audit);

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

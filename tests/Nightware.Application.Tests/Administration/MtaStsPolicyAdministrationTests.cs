using Nightware.Application.Administration;
using Nightware.Application.Administration.Entities;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Administration;

public sealed class MtaStsPolicyAdministrationTests
{
    private readonly AdminTestBed _bed = new();
    private readonly IMtaStsPolicies _policies = Substitute.For<IMtaStsPolicies>();

    private static CancellationToken Ct => AdminTestBed.Ct;

    private GetMtaStsCoverage Coverage => new(new GetEntityDnsRecords(_bed.Actor, _bed.Entities, _bed.MailDomains, _bed.PublicAddresses()), _policies);

    private AddDomainToMtaStsPolicy Add => new(_bed.Actor, Coverage, _policies, _bed.Audit);

    public MtaStsPolicyAdministrationTests()
    {
        _bed.MailDomains.GetDnsRecordsAsync(default!, Ct).ReturnsForAnyArgs(Result<IReadOnlyList<DnsRecord>>.Success(
            [new DnsRecord("acme.example", "MX", 3600, "10 mail.acme.example."), new DnsRecord("acme.example", "TXT", 3600, "v=spf1 mx -all")]));
        _policies.GetAsync(Arg.Any<CancellationToken>()).Returns(new MtaStsPolicy("testing", [], ["mail.server.example"]));
        _policies.SetMxHostsAsync(default!, Ct).ReturnsForAnyArgs(Result.Success());
    }

    [Fact]
    public async Task Coverage_names_the_domains_MX_hosts_the_policy_leaves_out()
    {
        _bed.ActAsEntityAdmin();

        MtaStsCoverage coverage = (await Coverage.ExecuteAsync(AdminTestBed.Acme, "acme.example", Ct)).Value;

        coverage.DomainMxHosts.ShouldBe(["mail.acme.example"]);
        coverage.Missing.ShouldBe(["mail.acme.example"]);
        coverage.NeedsHosts.ShouldBeTrue();
    }

    [Fact]
    public async Task Adding_keeps_the_hosts_the_policy_served_and_is_audited()
    {
        _bed.ActAsGlobal();

        MtaStsCoverage after = (await Add.ExecuteAsync(AdminTestBed.Acme, "acme.example", Ct)).Value;

        await _policies.Received(1).SetMxHostsAsync(Arg.Is<IReadOnlyList<string>>(h => h.SequenceEqual(new[] { "mail.server.example", "mail.acme.example" })), Ct);
        after.Missing.ShouldBeEmpty();
        after.NeedsHosts.ShouldBeFalse();
        AuditEvent audited = _bed.Audit.Single("mail.mta-sts.mx-hosts");
        audited.Succeeded.ShouldBeTrue();
        audited.Details!["added"].ShouldBe("mail.acme.example");
    }

    [Fact]
    public async Task A_covered_domain_writes_nothing()
    {
        _bed.ActAsGlobal();
        _policies.GetAsync(Arg.Any<CancellationToken>()).Returns(new MtaStsPolicy("enforce", ["*.acme.example"], ["mail.server.example"]));

        (await Add.ExecuteAsync(AdminTestBed.Acme, "acme.example", Ct)).Value.Missing.ShouldBeEmpty();

        await _policies.DidNotReceiveWithAnyArgs().SetMxHostsAsync(default!, Ct);
    }

    [Fact]
    public async Task Only_a_global_administrator_may_change_it_and_never_a_disabled_policy()
    {
        _bed.ActAsEntityAdmin();
        (await Add.ExecuteAsync(AdminTestBed.Acme, "acme.example", Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);

        _bed.ActAsGlobal();
        _policies.GetAsync(Arg.Any<CancellationToken>()).Returns(new MtaStsPolicy("disable", [], ["mail.server.example"]));
        (await Add.ExecuteAsync(AdminTestBed.Acme, "acme.example", Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);

        await _policies.DidNotReceiveWithAnyArgs().SetMxHostsAsync(default!, Ct);
    }

    [Fact]
    public async Task A_refused_write_is_reported_and_audited_as_failed()
    {
        _bed.ActAsGlobal();
        _policies.SetMxHostsAsync(default!, Ct).ReturnsForAnyArgs(Result.Failure(Error.Forbidden("sysMtaStsUpdate is missing.")));

        (await Add.ExecuteAsync(AdminTestBed.Acme, "acme.example", Ct)).Error!.Message.ShouldBe("sysMtaStsUpdate is missing.");
        _bed.Audit.Single("mail.mta-sts.mx-hosts").Succeeded.ShouldBeFalse();
    }
}

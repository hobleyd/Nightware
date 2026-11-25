using Nightware.Application.Administration.Reports;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Administration;

public sealed class MailReportTests
{
    private static readonly DateTimeOffset Day = new(2026, 11, 24, 0, 0, 0, TimeSpan.Zero);

    private readonly AdminTestBed _bed = new();

    private readonly IMailReports _reports = Substitute.For<IMailReports>();

    public MailReportTests()
    {
        _bed.Entities.ListAsync(Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<Entity>>.Success([AdminTestBed.AcmeEntity, AdminTestBed.OtherEntity]));
        _reports.ListAsync(Arg.Any<CancellationToken>()).Returns(new MailReports(
            [Dmarc("acme.example", Day), Dmarc("other.example", Day), Dmarc("mail.acme.example", Day.AddDays(1)), Dmarc("stranger.example", Day)],
            [new TlsReport("t1", "Google Inc.", "t", Day, Day.AddDays(1), null, [Policy("acme.example"), Policy("other.example")]), new TlsReport("t2", "Google Inc.", "u", Day, Day.AddDays(1), null, [Policy("other.example")])]));
    }

    private static CancellationToken Ct => AdminTestBed.Ct;

    private ListMailReports List => new(_bed.Actor, _bed.Entities, _reports);

    private static DmarcAggregateReport Dmarc(string domain, DateTimeOffset start) =>
        new("r-" + domain, "google.com", "1", domain, start, start.AddDays(1), null, new DmarcPublishedPolicy("reject", "reject", "strict", "strict", false), []);

    private static TlsPolicyResult Policy(string domain) => new("noPolicyFound", domain, [], 3, 0, []);

    [Fact]
    public async Task A_global_admin_sees_every_entitys_domains_newest_first()
    {
        _bed.ActAsGlobal();

        MailReportOverview overview = (await List.ExecuteAsync(null, Ct)).Value;

        overview.Domains.ShouldBe(["acme.example", "other.example"]);
        overview.Domain.ShouldBeNull();
        overview.Dmarc.Select(r => r.Domain).ShouldBe(["mail.acme.example", "acme.example", "other.example"]);
        overview.Tls.Select(r => r.Id).ShouldBe(["t1", "t2"]);
        overview.Summaries.Select(s => s.Domain).ShouldBe(["acme.example", "other.example"]);
        overview.Summaries[0].DmarcReports.ShouldBe(2);
    }

    [Fact]
    public async Task An_entity_admin_sees_only_their_domains_and_their_part_of_a_shared_TLS_report()
    {
        _bed.ActAsEntityAdmin();

        MailReportOverview overview = (await List.ExecuteAsync(null, Ct)).Value;

        overview.Domains.ShouldBe(["acme.example"]);
        overview.Dmarc.Select(r => r.Domain).ShouldBe(["mail.acme.example", "acme.example"]);
        overview.Tls.ShouldHaveSingleItem().Policies.ShouldHaveSingleItem().Domain.ShouldBe("acme.example");
    }

    [Fact]
    public async Task One_domain_can_be_asked_for_but_never_someone_elses()
    {
        _bed.ActAsEntityAdmin();

        MailReportOverview overview = (await List.ExecuteAsync("ACME.example", Ct)).Value;
        overview.Domain.ShouldBe("acme.example");
        overview.Summaries.ShouldHaveSingleItem().Domain.ShouldBe("acme.example");

        Result<MailReportOverview> refused = await List.ExecuteAsync("other.example", Ct);
        refused.Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task Someone_who_is_not_an_admin_is_refused_before_anything_is_read()
    {
        _bed.ActAsNobody();

        (await List.ExecuteAsync(null, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        await _reports.DidNotReceive().ListAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task No_domains_means_nothing_to_read()
    {
        _bed.ActAsGlobal();
        _bed.Entities.ListAsync(Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<Entity>>.Success([]));

        MailReportOverview overview = (await List.ExecuteAsync(null, Ct)).Value;

        overview.Domains.ShouldBeEmpty();
        await _reports.DidNotReceive().ListAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_mail_server_failure_is_passed_on()
    {
        _bed.ActAsGlobal();
        _reports.ListAsync(Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("down"));

        (await List.ExecuteAsync(null, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }
}

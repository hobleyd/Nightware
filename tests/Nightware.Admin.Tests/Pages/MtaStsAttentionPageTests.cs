using Microsoft.Extensions.DependencyInjection;

using Nightware.Admin.Tests.Support;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Admin.Tests.Pages;

/// <summary>
/// A domain whose senders' latest TLS reports found no MTA-STS policy: on the dashboard's worklist, explained on the
/// domain's DNS page (publish the records, or make the policy reachable), and on the reports page under its TLS table.
/// </summary>
public sealed class MtaStsAttentionPageTests : IDisposable
{
    private const string Token = "cf-SECRET-token-0123456789abcdefghijklmn";
    private static readonly DateTimeOffset Day = new(2026, 11, 24, 0, 0, 0, TimeSpan.Zero);

    private static readonly DnsRecord[] Needed =
    [
        new("acme.example", "MX", 3600, "10 mail.acme.example"),
        new("mta-sts.acme.example", "CNAME", 3600, "mail.acme.example"),
        new("_mta-sts.acme.example", "TXT", 3600, "v=STSv1; id=1"),
    ];

    /// <summary>Everything the DNS page would publish: the mail server's records and the default SPF and DMARC.</summary>
    private static readonly DnsRecord[] AllPublished =
    [
        .. Needed,
        new("acme.example", "TXT", 3600, "v=spf1 mx -all"),
        new("_dmarc.acme.example", "TXT", 3600, "v=DMARC1; p=reject; sp=reject; adkim=s; aspf=s; rua=mailto:postmaster@acme.example"),
    ];

    private readonly ConsoleHost _host = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string DnsUrl => $"/entities/{ConsoleHost.Acme}/dns/acme.example";

    public MtaStsAttentionPageTests()
    {
        _host.MailDomains.GetDnsRecordsAsync(default!, Ct).ReturnsForAnyArgs(Result<IReadOnlyList<DnsRecord>>.Success(Needed));
        _host.Entities.ListMembersAsync(ConsoleHost.Acme, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<IdentityAccount>>.Success([]));
    }

    public void Dispose() => _host.Dispose();

    private static TlsReport Report(int day, string type) =>
        new("t" + day, "Google Inc.", "t", Day.AddDays(day), Day.AddDays(day + 1), null, [new TlsPolicyResult(type, "acme.example", [], 3, 0, [])]);

    private void Reported(params TlsReport[] tls) => _host.Reports.ListAsync(Arg.Any<CancellationToken>()).Returns(new MailReports([], tls));

    /// <summary>Acme connected to Cloudflare, whose zone holds <paramref name="zone"/>.</summary>
    private void Connected(params DnsRecord[] zone)
    {
        _host.Entities.GetAsync(ConsoleHost.Acme, Arg.Any<CancellationToken>()).Returns(ConsoleHost.AcmeEntity with { DnsHost = DnsProvider.Cloudflare });
        string sealedToken = _host.Services.GetRequiredService<ISecretSealer>().Seal("dns-token:cloudflare:" + ConsoleHost.Acme, Token);
        _host.Entities.GetSealedDnsTokenAsync(default!, Ct).ReturnsForAnyArgs(Result<SealedDnsToken?>.Success(new SealedDnsToken(DnsProvider.Cloudflare, sealedToken)));
        _host.DnsHosting.FindZoneAsync(default!, default!, Ct).ReturnsForAnyArgs(new DnsZone("z-acme", "acme.example"));
        _host.DnsHosting.ListRecordsAsync(default!, default!, Ct).ReturnsForAnyArgs(Result<IReadOnlyList<ZoneRecord>>.Success([.. zone.Select((r, i) => new ZoneRecord($"r-{i}", r))]));
    }

    private async Task<string> GetAsync(string url, string sub = ConsoleHost.GlobalAdmin) =>
        await (await _host.ClientAs(sub).GetAsync(new Uri(url, UriKind.Relative), Ct)).Content.ReadAsStringAsync(Ct);

    [Fact]
    public async Task Unpublished_MTA_STS_records_are_published_from_the_DNS_page_the_dashboard_links_to()
    {
        Connected(Needed[0]);
        Reported(Report(0, "noPolicyFound"));

        string dashboard = await GetAsync("/");
        dashboard.ShouldContain("senders found no MTA-STS policy for it, so they may deliver its mail unencrypted");
        dashboard.ShouldContain($"href=\"{DnsUrl}\">Review and publish</a>");
        dashboard.ShouldContain("href=\"/reports?domain=acme.example\">TLS reports</a>");

        string dns = await GetAsync(DnsUrl);
        dns.ShouldContain("<div class=\"warning\" id=\"mta-sts\">");
        dns.ShouldContain("Its MTA-STS records are not all at Cloudflare yet. Publishing the changes below adds them.");
        dns.ShouldContain("<td data-label=\"Name\">_mta-sts.acme.example</td>");
    }

    [Fact]
    public async Task Published_records_point_at_the_policy_host_instead()
    {
        Connected(AllPublished);
        Reported(Report(0, "noPolicyFound"));

        string dashboard = await GetAsync("/");
        dashboard.ShouldContain($"href=\"{DnsUrl}#mta-sts\">Fix MTA-STS</a>");
        dashboard.ShouldNotContain("not yet published to the DNS host");

        string dns = await GetAsync(DnsUrl);
        dns.ShouldContain("Its MTA-STS records are published at Cloudflare, so the policy itself is not being served.");
        dns.ShouldContain("href=\"https://mta-sts.acme.example/.well-known/mta-sts.txt\"");
    }

    [Fact]
    public async Task Once_the_latest_reports_find_the_policy_nothing_needs_attention()
    {
        Connected(AllPublished);
        Reported(Report(0, "noPolicyFound"), Report(1, "sts"));

        (await GetAsync("/")).ShouldNotContain("MTA-STS");
        (await GetAsync(DnsUrl)).ShouldNotContain("id=\"mta-sts\"");
    }

    [Fact]
    public async Task An_entity_admin_is_told_to_ask_the_operator()
    {
        Reported(Report(0, "noPolicyFound"));

        string dns = await GetAsync(DnsUrl, ConsoleHost.EntityAdmin);

        dns.ShouldContain("Senders found no MTA-STS policy for acme.example</strong>");
        dns.ShouldContain("Ask your Nightware operator to publish the domain's MTA-STS records and make its policy reachable.");
    }

    [Fact]
    public async Task The_reports_page_explains_none_found_under_the_TLS_table_and_links_each_domain_to_its_DNS_page()
    {
        Reported(Report(0, "noPolicyFound"));

        string html = await GetAsync("/reports", ConsoleHost.EntityAdmin);

        html.ShouldContain("<section class=\"panel reports\">");
        html.ShouldContain($"<a href=\"{DnsUrl}#mta-sts\">acme.example</a>");
        int table = html.IndexOf("aria-label=\"TLS reports\"", StringComparison.Ordinal);
        int note = html.IndexOf("id=\"no-tls-policy\"", StringComparison.Ordinal);
        table.ShouldBeGreaterThan(0);
        note.ShouldBeGreaterThan(table);
        html.ShouldNotContain("Older reports keep saying so");

        Reported(Report(0, "noPolicyFound"), Report(1, "sts"));
        string fixedSince = await GetAsync("/reports", ConsoleHost.EntityAdmin);
        fixedSince.ShouldContain("Older reports keep saying so after it is fixed");
        fixedSince.ShouldContain($"<a href=\"{DnsUrl}\">acme.example</a>");
    }
}

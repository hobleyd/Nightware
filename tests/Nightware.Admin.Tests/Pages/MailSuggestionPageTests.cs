using System.Net;

using Nightware.Admin.Tests.Support;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Admin.Tests.Pages;

/// <summary>The hardened SPF and DMARC the DNS page offers, prefilled and labelled, to a domain with none of its own.</summary>
public sealed class MailSuggestionPageTests : IDisposable
{
    private const string PublicHost = "mail.operator.example";

    private readonly ConsoleHost _host = new() { PublicHost = PublicHost };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string DnsUrl => $"/entities/{ConsoleHost.Acme}/dns/acme.example";

    private static MailDomain Acme => ConsoleHost.Domain("acme.example");

    public MailSuggestionPageTests()
    {
        _host.MailDomains.GetDnsRecordsAsync(default!, Ct).ReturnsForAnyArgs(Result<IReadOnlyList<DnsRecord>>.Success(
        [
            new DnsRecord("acme.example.", "MX", 3600, "10 mail.example."),
            new DnsRecord("acme.example.", "TXT", 3600, "v=spf1 mx ra=postmaster -all"),
            new DnsRecord("_dmarc.acme.example.", "TXT", 3600, "v=DMARC1; p=reject"),
        ]));
        _host.PublicDns.ResolveAsync(PublicHost, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<IPAddress>>.Success([IPAddress.Parse("81.2.69.160")]));
    }

    public void Dispose() => _host.Dispose();

    private async Task<string> GetAsync(string url, string sub = ConsoleHost.GlobalAdmin) =>
        await (await _host.ClientAs(sub).GetAsync(new Uri(url, UriKind.Relative), Ct)).Content.ReadAsStringAsync(Ct);

    private static string FormField(string name, string value) => $"name=\"{name}\">{Environment.NewLine}{value}</textarea>";

    [Fact]
    public async Task A_domain_without_its_own_records_is_offered_the_hardened_pair_labelled_as_not_in_use()
    {
        string html = await GetAsync(DnsUrl);

        html.ShouldContain(FormField("Spf", "v=spf1 ip4:81.2.69.160 -all"));
        html.ShouldContain(FormField("Dmarc", "v=DMARC1; p=reject; sp=reject; adkim=s; aspf=s; rua=mailto:postmaster@acme.example"));
        html.ShouldContain("Suggested, not in use until you save: only this server&#x27;s public address may send");
        html.ShouldContain("The address comes from public DNS for mail.operator.example. Behind NAT");
        html.ShouldContain("Saving makes both fields this domain's own records, suggestions included.");
        html.ShouldContain("Now: the mail server's record <code>v=spf1 mx ra=postmaster -all</code>");
    }

    [Fact]
    public async Task A_cloudflare_token_that_cannot_be_opened_falls_back_to_public_dns()
    {
        _host.Entities.GetAsync(ConsoleHost.Acme, Arg.Any<CancellationToken>()).Returns(ConsoleHost.AcmeEntity with { HasDnsToken = true });
        _host.Entities.GetSealedDnsTokenAsync(ConsoleHost.Acme, Arg.Any<CancellationToken>()).Returns(Result<string?>.Success(null));

        // The token cannot be opened here, so the zone is skipped and public DNS answers instead.
        (await GetAsync(DnsUrl)).ShouldContain("from public DNS for mail.operator.example");
    }

    [Fact]
    public async Task Without_a_public_address_the_mx_hosts_stand_in_and_the_page_says_why()
    {
        _host.PublicDns.ResolveAsync(PublicHost, Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("timed out"));

        string html = await GetAsync(DnsUrl);

        html.ShouldContain(FormField("Spf", "v=spf1 mx -all"));
        html.ShouldContain("No public address was found for mail.operator.example, so the MX hosts stand in.");
    }

    [Fact]
    public async Task A_record_the_domain_already_chose_is_never_replaced_by_a_suggestion()
    {
        _host.Entities.GetAsync(ConsoleHost.Acme, Arg.Any<CancellationToken>()).Returns(ConsoleHost.AcmeEntity with
        {
            MailPolicies = new Dictionary<MailDomain, DomainMailPolicy> { [Acme] = new(SpfRecord.Parse("v=spf1 include:mailer.example -all").Value, null) },
        });

        string html = await GetAsync(DnsUrl);

        html.ShouldContain(FormField("Spf", "v=spf1 include:mailer.example -all"));
        html.ShouldNotContain("id=\"spf-suggested\"");
        html.ShouldContain("id=\"dmarc-suggested\"");
    }

    [Fact]
    public async Task What_was_typed_survives_a_refused_save()
    {
        HttpClient client = _host.ClientAs(ConsoleHost.GlobalAdmin);

        string html = await (await ConsoleHost.PostFormAsync(client, DnsUrl, DnsUrl + "?handler=MailPolicy", new Dictionary<string, string> { ["Spf"] = "not an spf record", ["Dmarc"] = "" })).Content.ReadAsStringAsync(Ct);

        html.ShouldContain(FormField("Spf", "not an spf record"));
        html.ShouldNotContain(FormField("Spf", "v=spf1 ip4:81.2.69.160 -all"));
    }

    [Fact]
    public async Task An_entity_admin_is_offered_nothing_and_nothing_is_looked_up()
    {
        string html = await GetAsync(DnsUrl, ConsoleHost.EntityAdmin);

        html.ShouldNotContain("Suggested, not in use");
        await _host.PublicDns.DidNotReceiveWithAnyArgs().ResolveAsync(default!, Ct);
    }
}

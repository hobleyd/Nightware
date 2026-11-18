using System.Net;

using Nightware.Admin.Tests.Support;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Admin.Tests.Pages;

/// <summary>A domain's SPF and DMARC records: shown on the entity and DNS pages, edited by global admins on the DNS page.</summary>
public sealed class MailPolicyPageTests : IDisposable
{
    private readonly ConsoleHost _host = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string DnsUrl => $"/entities/{ConsoleHost.Acme}/dns/acme.example";

    private static MailDomain Acme => ConsoleHost.Domain("acme.example");

    public MailPolicyPageTests() =>
        _host.MailDomains.GetDnsRecordsAsync(default!, Ct).ReturnsForAnyArgs(Result<IReadOnlyList<DnsRecord>>.Success(
        [
            new DnsRecord("acme.example.", "MX", 3600, "10 mail.example."),
            new DnsRecord("acme.example.", "TXT", 3600, "v=spf1 mx ra=postmaster -all"),
            new DnsRecord("_dmarc.acme.example.", "TXT", 3600, "v=DMARC1; p=reject"),
        ]));

    public void Dispose() => _host.Dispose();

    private async Task<string> GetAsync(string url, string sub = ConsoleHost.GlobalAdmin) =>
        await (await _host.ClientAs(sub).GetAsync(new Uri(url, UriKind.Relative), Ct)).Content.ReadAsStringAsync(Ct);

    /// <summary>The form's textarea holding <paramref name="value"/> (the tag helper starts its content on a new line).</summary>
    private static string FormField(string name, string value) => $"name=\"{name}\">{Environment.NewLine}{value}</textarea>";

    private void GivenChosen(DomainMailPolicy policy) =>
        _host.Entities.GetAsync(ConsoleHost.Acme, Arg.Any<CancellationToken>()).Returns(ConsoleHost.AcmeEntity with
        {
            MailPolicies = new Dictionary<MailDomain, DomainMailPolicy> { [Acme] = policy },
        });

    [Fact]
    public async Task The_entity_page_says_whose_spf_and_dmarc_each_domain_uses()
    {
        string plain = await GetAsync($"/entities/{ConsoleHost.Acme}");
        plain.ShouldContain("<th>SPF</th><th>DMARC</th>");
        plain.ShouldContain("Mail server&#x27;s");

        GivenChosen(new DomainMailPolicy(null, DmarcRecord.Parse("v=DMARC1; p=quarantine").Value));
        (await GetAsync($"/entities/{ConsoleHost.Acme}")).ShouldContain("Custom (p=quarantine)");
    }

    [Fact]
    public async Task A_global_admin_sees_the_mail_servers_records_stated_above_the_form()
    {
        string html = await GetAsync(DnsUrl);

        html.ShouldContain("<h2>1. Decide SPF and DMARC</h2>");
        html.ShouldContain("handler=MailPolicy");
        html.ShouldContain("Now: the mail server's record <code>v=spf1 mx ra=postmaster -all</code>");
        html.ShouldContain("Now: the mail server's record <code>v=DMARC1; p=reject</code>");
        html.ShouldContain("It is strict: mail that fails the checks is refused.");
        html.ShouldNotContain("placeholder=\"v=");
        html.ShouldNotContain("handler=DefaultMailPolicy");
    }

    [Fact]
    public async Task A_chosen_record_is_named_as_the_domains_own()
    {
        GivenChosen(new DomainMailPolicy(null, DmarcRecord.Parse("v=DMARC1; p=quarantine").Value));

        string html = await GetAsync(DnsUrl);

        html.ShouldContain("Now: this domain's own record, below.");
        html.ShouldNotContain("It is strict");
    }

    [Fact]
    public async Task The_records_to_publish_sit_in_their_own_panel()
    {
        (await GetAsync(DnsUrl)).ShouldContain("<h2>2. Publish these records</h2>");
    }

    [Fact]
    public async Task A_global_admin_decides_spf_and_dmarc_before_the_records_to_publish()
    {
        string global = await GetAsync(DnsUrl);
        global.IndexOf("1. Decide SPF and DMARC", StringComparison.Ordinal).ShouldBeLessThan(global.IndexOf("2. Publish these records", StringComparison.Ordinal));
        global.ShouldContain("<summary>How SPF and DMARC work</summary>");

        string entityAdmin = await GetAsync(DnsUrl, ConsoleHost.EntityAdmin);
        entityAdmin.IndexOf("<h2>Publish these records</h2>", StringComparison.Ordinal).ShouldBeLessThan(entityAdmin.IndexOf("<h2>SPF and DMARC</h2>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Each_record_field_is_labelled_with_the_record_it_belongs_to()
    {
        string html = await GetAsync(DnsUrl);

        html.ShouldContain("aria-label=\"MX record name\"");
        html.ShouldContain("aria-label=\"TXT record value at _dmarc.acme.example.\"");
        html.ShouldNotContain("aria-label=\"Name\"");
    }

    [Fact]
    public async Task A_chosen_record_replaces_the_mail_servers_in_the_table_and_fills_the_form()
    {
        GivenChosen(new DomainMailPolicy(SpfRecord.Parse("v=spf1 mx include:mailer.example -all").Value, null));

        string html = await GetAsync(DnsUrl);

        html.ShouldNotContain("ra=postmaster");
        html.ShouldContain(FormField("Spf", "v=spf1 mx include:mailer.example -all"));
        html.ShouldContain("handler=DefaultMailPolicy");
    }

    [Fact]
    public async Task An_entity_admin_sees_which_records_are_in_use_but_no_form()
    {
        GivenChosen(new DomainMailPolicy(null, DmarcRecord.Parse("v=DMARC1; p=none").Value));

        string html = await GetAsync(DnsUrl, ConsoleHost.EntityAdmin);

        html.ShouldContain("set for this domain (p=none)");
        html.ShouldContain("Only your Nightware operator can change them.");
        html.ShouldNotContain("handler=MailPolicy");
    }

    [Fact]
    public async Task Saving_stores_the_records_and_returns_to_the_dns_page()
    {
        _host.Entities.SetMailPolicyAsync(default!, default!, default!, Ct).ReturnsForAnyArgs(ConsoleHost.AcmeEntity);

        using HttpResponseMessage response = await ConsoleHost.PostFormAsync(_host.ClientAs(ConsoleHost.GlobalAdmin), DnsUrl, DnsUrl + "?handler=MailPolicy",
            new Dictionary<string, string> { ["Spf"] = "v=spf1 mx -all", ["Dmarc"] = "", ["ConfirmSpf"] = "true" });

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.ShouldEndWith(DnsUrl);
        await _host.Entities.Received(1).SetMailPolicyAsync(
            ConsoleHost.Acme, Acme, Arg.Is<DomainMailPolicy>(p => p.Spf!.Value == "v=spf1 mx -all" && p.Dmarc == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_malformed_record_is_shown_with_what_was_typed()
    {
        using HttpResponseMessage response = await ConsoleHost.PostFormAsync(_host.ClientAs(ConsoleHost.GlobalAdmin), DnsUrl, DnsUrl + "?handler=MailPolicy",
            new Dictionary<string, string> { ["Spf"] = "", ["Dmarc"] = "v=DMARC1; p=block" });

        string html = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain("p= is one of none, quarantine or reject.");
        html.ShouldContain(FormField("Dmarc", "v=DMARC1; p=block"));
        await _host.Entities.DidNotReceiveWithAnyArgs().SetMailPolicyAsync(default!, default!, default!, Ct);
    }

    [Fact]
    public async Task Using_the_mail_servers_records_clears_the_choice()
    {
        _host.Entities.SetMailPolicyAsync(default!, default!, default!, Ct).ReturnsForAnyArgs(ConsoleHost.AcmeEntity);

        using HttpResponseMessage response = await ConsoleHost.PostFormAsync(_host.ClientAs(ConsoleHost.GlobalAdmin), DnsUrl, DnsUrl + "?handler=DefaultMailPolicy",
            new Dictionary<string, string> { ["Spf"] = "v=spf1 -all" });

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await _host.Entities.Received(1).SetMailPolicyAsync(ConsoleHost.Acme, Acme, DomainMailPolicy.MailServerDefault, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_entity_admin_cannot_save_records()
    {
        using HttpResponseMessage response = await ConsoleHost.PostFormAsync(_host.ClientAs(ConsoleHost.EntityAdmin), DnsUrl, DnsUrl + "?handler=MailPolicy",
            new Dictionary<string, string> { ["Spf"] = "v=spf1 -all" });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await _host.Entities.DidNotReceiveWithAnyArgs().SetMailPolicyAsync(default!, default!, default!, Ct);
    }

    [Fact]
    public async Task Changing_spf_without_the_confirming_box_changes_nothing()
    {
        string html = await (await ConsoleHost.PostFormAsync(_host.ClientAs(ConsoleHost.GlobalAdmin), DnsUrl, DnsUrl + "?handler=MailPolicy",
            new Dictionary<string, string> { ["Spf"] = "v=spf1 ip4:81.2.69.160 -all", ["Dmarc"] = "" })).Content.ReadAsStringAsync(Ct);

        html.ShouldContain("Tick the box to confirm the SPF record: it decides who may send mail for acme.example.");
        html.ShouldContain(FormField("Spf", "v=spf1 ip4:81.2.69.160 -all"));
        html.ShouldContain("name=\"ConfirmSpf\" value=\"true\" required");
        await _host.Entities.DidNotReceiveWithAnyArgs().SetMailPolicyAsync(default!, default!, default!, Ct);
    }

    [Fact]
    public async Task Saving_only_dmarc_beside_the_domains_own_spf_needs_no_box()
    {
        GivenChosen(new DomainMailPolicy(SpfRecord.Parse("v=spf1 mx -all").Value, null));
        _host.Entities.SetMailPolicyAsync(default!, default!, default!, Ct).ReturnsForAnyArgs(ConsoleHost.AcmeEntity);

        string page = await GetAsync(DnsUrl);
        page.ShouldNotContain("name=\"ConfirmSpf\"");

        using HttpResponseMessage response = await ConsoleHost.PostFormAsync(_host.ClientAs(ConsoleHost.GlobalAdmin), DnsUrl, DnsUrl + "?handler=MailPolicy",
            new Dictionary<string, string> { ["Spf"] = "v=spf1 mx -all", ["Dmarc"] = "v=DMARC1; p=reject" });
        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task The_save_is_secondary_and_each_field_is_described_by_what_is_live()
    {
        string html = await GetAsync(DnsUrl);

        html.ShouldContain("<button type=\"submit\" class=\"secondary\">Save</button>");
        html.ShouldContain("aria-describedby=\"spf-now");
        html.ShouldContain("aria-describedby=\"dmarc-now");
    }
}

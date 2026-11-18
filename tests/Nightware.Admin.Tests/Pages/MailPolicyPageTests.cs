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
        plain.ShouldContain("Mail server's");

        GivenChosen(new DomainMailPolicy(null, DmarcRecord.Parse("v=DMARC1; p=quarantine").Value));
        (await GetAsync($"/entities/{ConsoleHost.Acme}")).ShouldContain("<span class=\"chip missing\" title=\"No rua= address, so nobody receives reports of mail that fails.\">Present</span>");
    }

    [Fact]
    public async Task The_entity_page_colours_the_domains_own_spf_and_dmarc_by_what_they_leave_open()
    {
        GivenChosen(new DomainMailPolicy(SpfRecord.Parse("v=spf1 mx -all").Value, DmarcRecord.Default(ConsoleHost.Domain("acme.example"))));
        string strict = await GetAsync($"/entities/{ConsoleHost.Acme}");
        strict.Split("<span class=\"chip linked\">Present</span>").Length.ShouldBe(3);
        strict.ShouldNotContain("Custom");

        GivenChosen(new DomainMailPolicy(SpfRecord.Parse("v=spf1 +all").Value, null));
        string open = await GetAsync($"/entities/{ConsoleHost.Acme}");
        open.ShouldContain("<span class=\"chip error\" title=\"&#x2B;all lets any server send mail as the domain.\">Present</span>");
        open.ShouldContain("<span class=\"visually-hidden\">, unsafe: &#x2B;all lets any server send mail as the domain.</span>");
    }

    [Fact]
    public async Task A_global_admin_sees_the_mail_servers_records_stated_above_the_form()
    {
        string html = await GetAsync(DnsUrl);

        html.ShouldContain("<h2>Decide SPF and DMARC</h2>");
        html.ShouldContain("handler=MailPolicy");
        html.ShouldContain("Now: the mail server&#x27;s record <code>v=spf1 mx ra=postmaster -all</code>");
        html.ShouldContain("Now: the mail server&#x27;s record <code>v=DMARC1; p=reject</code>");
        html.ShouldNotContain("It is strict");
        html.ShouldContain("<span class=\"chip missing\">Weak</span> No rua= address");
        html.ShouldNotContain("placeholder=\"v=");
        html.ShouldNotContain("handler=DefaultMailPolicy");
    }

    [Fact]
    public async Task A_chosen_record_is_named_as_the_domains_own()
    {
        GivenChosen(new DomainMailPolicy(null, DmarcRecord.Parse("v=DMARC1; p=quarantine").Value));

        string html = await GetAsync(DnsUrl);

        html.ShouldContain("Now: this domain&#x27;s own record <code>");
        html.ShouldNotContain("It is strict");
    }

    [Fact]
    public async Task The_records_sit_in_their_own_panel()
    {
        (await GetAsync(DnsUrl)).ShouldContain("<h2>In use now (before you save)</h2>");
    }

    [Fact]
    public async Task Deciding_spf_and_dmarc_is_a_step_only_until_the_domain_has_both()
    {
        string undecided = await GetAsync(DnsUrl);
        undecided.ShouldContain("<h2>Decide SPF and DMARC</h2>");
        undecided.IndexOf("Decide SPF and DMARC", StringComparison.Ordinal).ShouldBeLessThan(undecided.IndexOf("In use now (before you save)", StringComparison.Ordinal));

        GivenChosen(new DomainMailPolicy(SpfRecord.Parse("v=spf1 mx -all").Value, DmarcRecord.Parse("v=DMARC1; p=reject; rua=mailto:postmaster@acme.example").Value));
        string decided = await GetAsync(DnsUrl);
        decided.ShouldNotContain("handler=MailPolicy");
        decided.ShouldContain("Edit SPF and DMARC</a>");

        string editing = await GetAsync(DnsUrl + "?edit=true");
        editing.ShouldContain("handler=MailPolicy");
        editing.ShouldContain(">Cancel</a>");
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
    public async Task An_entity_admin_sees_the_records_and_their_weaknesses_but_no_form()
    {
        GivenChosen(new DomainMailPolicy(null, DmarcRecord.Parse("v=DMARC1; p=none").Value));

        string html = await GetAsync(DnsUrl, ConsoleHost.EntityAdmin);

        html.ShouldContain("Only your Nightware operator can change SPF and DMARC.");
        html.ShouldContain("p=none only reports mail that fails");
        html.ShouldNotContain("handler=MailPolicy");
        html.ShouldNotContain("Edit SPF and DMARC");
    }

    [Fact]
    public async Task A_weak_record_is_marked_where_it_is_read_with_the_way_to_fix_it()
    {
        GivenChosen(new DomainMailPolicy(SpfRecord.Parse("v=spf1 mx ~all").Value, DmarcRecord.Parse("v=DMARC1; p=none").Value));

        string html = await GetAsync(DnsUrl);

        html.ShouldContain("<span class=\"chip missing\">Weak</span> ~all only soft-fails unlisted senders");
        html.ShouldContain("<span class=\"chip missing\">Weak</span> p=none only reports mail that fails");
        html.ShouldContain("href=\"/entities/e-acme/dns/acme.example?edit=true#mail-policy\">Edit SPF and DMARC</a></p>");
    }

    [Fact]
    public async Task An_spf_record_that_lets_anyone_send_is_marked_unsafe()
    {
        GivenChosen(new DomainMailPolicy(SpfRecord.Parse("v=spf1 +all").Value, DmarcRecord.Parse("v=DMARC1; p=reject; rua=mailto:postmaster@acme.example").Value));

        (await GetAsync(DnsUrl)).ShouldContain("<span class=\"chip error\">Unsafe</span> &#x2B;all lets any server send mail as the domain.");
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
    public async Task A_new_spf_record_is_reviewed_before_it_is_saved_with_a_box_describing_that_record()
    {
        string html = await (await ConsoleHost.PostFormAsync(_host.ClientAs(ConsoleHost.GlobalAdmin), DnsUrl, DnsUrl + "?handler=MailPolicy",
            new Dictionary<string, string> { ["Spf"] = "v=spf1 ip4:81.2.69.160 include:mailer.example ~all", ["Dmarc"] = "" })).Content.ReadAsStringAsync(Ct);

        html.ShouldContain("<h2>Use this SPF record?</h2>");
        html.ShouldContain("<code>v=spf1 ip4:81.2.69.160 include:mailer.example ~all</code>");
        html.ShouldContain("name=\"ConfirmSpf\" value=\"true\" required> Only 81.2.69.160 and whatever mailer.example lists may send mail for acme.example; other senders only soft-fail");
        html.ShouldContain("<span class=\"chip missing\">Weak</span> ~all only soft-fails");
        html.ShouldContain("type=\"hidden\" name=\"Spf\" value=\"v=spf1 ip4:81.2.69.160 include:mailer.example ~all\"");
        html.ShouldNotContain("validation-summary-errors");
        await _host.Entities.DidNotReceiveWithAnyArgs().SetMailPolicyAsync(default!, default!, default!, Ct);
    }

    [Fact]
    public async Task Clearing_spf_to_keep_the_mail_servers_record_needs_no_review()
    {
        _host.Entities.SetMailPolicyAsync(default!, default!, default!, Ct).ReturnsForAnyArgs(ConsoleHost.AcmeEntity);

        using HttpResponseMessage response = await ConsoleHost.PostFormAsync(_host.ClientAs(ConsoleHost.GlobalAdmin), DnsUrl, DnsUrl + "?handler=MailPolicy",
            new Dictionary<string, string> { ["Spf"] = "", ["Dmarc"] = "v=DMARC1; p=reject" });

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
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
    public async Task When_deciding_is_the_only_step_save_leads_and_each_field_is_described_by_what_is_live()
    {
        string html = await GetAsync(DnsUrl);

        html.ShouldContain("<button type=\"submit\">Save</button>");
        html.ShouldContain("aria-describedby=\"spf-now");
        html.ShouldContain("aria-describedby=\"dmarc-now");
    }

    [Fact]
    public async Task What_is_in_use_now_carries_the_same_weakness_chips_as_the_records()
    {
        GivenChosen(new DomainMailPolicy(null, DmarcRecord.Parse("v=DMARC1; p=none").Value));

        string html = await GetAsync(DnsUrl);

        html.ShouldContain("Now: this domain&#x27;s own record <code>v=DMARC1; p=none</code>");
        html.ShouldNotContain("It is strict");
        html.Split("Leave a field blank to keep the mail server").Length.ShouldBe(2);
    }
}

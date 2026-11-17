using System.Net;
using System.Text.RegularExpressions;

using Nightware.Admin.Tests.Support;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Admin.Tests.Pages;

public sealed partial class DashboardAndReadFailureTests : IDisposable
{
    private readonly ConsoleHost _host = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string DnsUrl => $"/entities/{ConsoleHost.Acme}/dns/acme.example";

    public void Dispose() => _host.Dispose();

    /// <summary>The breadcrumb's words, tags stripped and spacing collapsed.</summary>
    private static string Crumbs(string html) =>
        WhiteSpace().Replace(Tags().Replace(Regex.Match(html, "<nav class=\"crumbs\"[^>]*>(.*?)</nav>", RegexOptions.Singleline).Groups[1].Value, ""), " ").Trim();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();

    private async Task<string> GetAsync(string url, string sub = ConsoleHost.GlobalAdmin) =>
        await (await _host.ClientAs(sub).GetAsync(new Uri(url, UriKind.Relative), Ct)).Content.ReadAsStringAsync(Ct);

    private void Members(params IdentityAccount[] members) =>
        _host.Entities.ListMembersAsync(ConsoleHost.Acme, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<IdentityAccount>>.Success(members));

    [Fact]
    public async Task Dns_records_that_cannot_be_read_are_not_shown_as_missing()
    {
        _host.MailDomains.GetDnsRecordsAsync(default!, Ct).ReturnsForAnyArgs(Error.BackendUnavailable("Connection refused (mail.example:443)."));

        string html = await GetAsync(DnsUrl);

        html.ShouldContain("Could not read this page");
        html.ShouldContain("Connection refused (mail.example:443).");
        html.ShouldContain("Try again");
        html.ShouldNotContain("no records for this domain yet");
        Crumbs(html).ShouldBe("Entities › Acme Pty › DNS records for acme.example");
    }

    [Fact]
    public async Task A_dns_page_whose_entity_cannot_be_read_keeps_a_way_back_to_it()
    {
        _host.Entities.GetAsync(ConsoleHost.Acme, Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("Keycloak is down."));

        string html = await GetAsync(DnsUrl);

        Crumbs(html).ShouldBe("Entities › Entity › DNS records for acme.example");
        html.ShouldContain("<a href=\"/entities/e-acme\">Entity</a>");
    }

    [Fact]
    public async Task A_domain_served_with_keys_but_no_records_says_repair_would_not_help()
    {
        _host.MailDomains.GetDnsRecordsAsync(default!, Ct).ReturnsForAnyArgs(Result<IReadOnlyList<DnsRecord>>.Success([]));

        string html = await GetAsync(DnsUrl);
        html.ShouldContain("returned no records for it. Repair would not change that.");
        html.ShouldNotContain("handler=Repair");
        html.ShouldNotContain("Publish these");
        (await GetAsync(DnsUrl, ConsoleHost.EntityAdmin)).ShouldContain("Tell your Nightware operator.");
    }

    [Fact]
    public async Task An_unserved_domain_offers_repair_on_its_dns_page_without_asking_for_records()
    {
        _host.MailDomains.FindAsync(ConsoleHost.Domain("acme.example"), Arg.Any<CancellationToken>()).Returns(Error.NotFound("Not on the mail server."));

        string html = await GetAsync(DnsUrl);
        html.ShouldContain("does not serve acme.example yet");
        html.ShouldContain("handler=Repair");

        string entityAdmin = await GetAsync(DnsUrl, ConsoleHost.EntityAdmin);
        entityAdmin.ShouldContain("Ask your Nightware operator to repair the domain.");
        entityAdmin.ShouldNotContain("handler=Repair");
        await _host.MailDomains.DidNotReceiveWithAnyArgs().GetDnsRecordsAsync(default!, Ct);
    }

    [Fact]
    public async Task A_domain_without_keys_shows_its_records_under_a_repair_warning()
    {
        _host.MailDomains.FindAsync(ConsoleHost.Domain("acme.example"), Arg.Any<CancellationToken>()).Returns(new MailDomainInfo("d", ConsoleHost.Domain("acme.example"), false));
        _host.MailDomains.GetDnsRecordsAsync(default!, Ct).ReturnsForAnyArgs(Result<IReadOnlyList<DnsRecord>>.Success([new DnsRecord("acme.example.", "MX", 3600, "10 mail.example.")]));

        string html = await GetAsync(DnsUrl);

        html.ShouldContain("has not loaded its signing (DKIM) keys");
        html.ShouldContain("handler=Repair");
        html.ShouldContain("10 mail.example.");
    }

    [Fact]
    public async Task Repair_on_the_dns_page_returns_to_it_with_what_it_achieved()
    {
        _host.Entities.AddDomainAsync(default!, default!, Ct).ReturnsForAnyArgs(ConsoleHost.AcmeEntity);

        using HttpResponseMessage response = await ConsoleHost.PostFormAsync(_host.ClientAs(ConsoleHost.GlobalAdmin), DnsUrl, DnsUrl + "?handler=Repair", new Dictionary<string, string>());

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.ShouldEndWith(DnsUrl);
    }

    [Fact]
    public async Task Global_admins_that_cannot_be_read_are_not_shown_as_an_empty_table()
    {
        _host.Identities.ListGlobalAdminsAsync(Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("Keycloak is down."));

        string html = await GetAsync("/admins");

        html.ShouldContain("Could not read this page");
        html.ShouldContain("<h1>Global administrators</h1>");
        html.ShouldNotContain("<table");
    }

    [Fact]
    public async Task Errors_are_shown_under_the_page_heading()
    {
        _host.AddUser("u-dan", "dan", "dan@acme.example");
        HttpClient client = _host.ClientAs(ConsoleHost.GlobalAdmin);

        string html = await (await ConsoleHost.PostFormAsync(client, "/users/u-dan", "/users/u-dan?handler=Offboard", new Dictionary<string, string>())).Content.ReadAsStringAsync(Ct);

        int heading = html.IndexOf("</h1>", StringComparison.Ordinal);
        int error = html.IndexOf("Tick the box to confirm the offboarding.", StringComparison.Ordinal);
        heading.ShouldBeGreaterThan(0);
        error.ShouldBeGreaterThan(heading);
        html.Split("validation-summary").Length.ShouldBe(2);
    }

    [Fact]
    public async Task Only_domains_no_address_is_on_are_offered_for_removal()
    {
        _host.Entities.GetAsync(ConsoleHost.Acme, Arg.Any<CancellationToken>()).Returns(new Entity(ConsoleHost.Acme, "acme", "Acme Pty", [ConsoleHost.Domain("acme.example"), ConsoleHost.Domain("spare.example")]));
        Members(new IdentityAccount("u-dan", "dan", "dan@acme.example", true));

        string html = await GetAsync("/entities/" + ConsoleHost.Acme);

        html.ShouldContain("aria-label=\"Remove spare.example\"");
        html.ShouldNotContain("aria-label=\"Remove acme.example\"");
    }

    [Fact]
    public async Task When_every_domain_is_in_use_no_removal_is_offered()
    {
        Members(new IdentityAccount("u-dan", "dan", "dan@acme.example", true));

        string html = await GetAsync("/entities/" + ConsoleHost.Acme);

        html.ShouldNotContain("handler=RemoveDomain");
    }

    [Fact]
    public async Task A_system_that_answers_its_health_check_but_failed_for_a_user_is_not_shown_as_ok()
    {
        _host.AddUser("u-eve", "eve", "eve@acme.example", enabled: false);
        _host.Mail.FindByAddressAsync("eve@acme.example", Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("Connection refused."));
        Members(new IdentityAccount("u-eve", "eve", "eve@acme.example", false));

        string html = await GetAsync("/");

        html.ShouldContain("Reachable, failed for 1");
        html.ShouldContain("Mail: Failed");
        html.ShouldContain("<span class=\"hint\"> · Connection refused.</span>");
        html.ShouldNotContain("If this persists");
        html.ShouldNotContain(">Unreachable<");
        html.ShouldContain("answers its health check but failed to answer for eve");
    }

    [Fact]
    public async Task Worklist_rows_carry_the_fix_that_is_safe_to_repeat()
    {
        _host.AddUser("u-eve", "eve", "eve@acme.example", enabled: false);
        _host.Mail.FindByAddressAsync("eve@acme.example", Arg.Any<CancellationToken>()).Returns(new MailAccount("m-eve", "eve@acme.example", true));
        _host.AddUser("u-dan", "dan", "dan@acme.example");
        Members(new IdentityAccount("u-eve", "eve", "eve@acme.example", false), new IdentityAccount("u-dan", "dan", "dan@acme.example", true));

        string html = await GetAsync("/");

        html.ShouldContain("handler=FinishOffboarding");
        html.ShouldContain("handler=RepairUser");
    }

    [Fact]
    public async Task Finishing_an_offboarding_from_the_dashboard_shuts_what_is_still_open()
    {
        _host.AddUser("u-eve", "eve", "eve@acme.example", enabled: false);
        _host.Mail.FindByAddressAsync("eve@acme.example", Arg.Any<CancellationToken>()).Returns(new MailAccount("m-eve", "eve@acme.example", true));
        _host.Mail.SetEnabledAsync(default!, default, Ct).ReturnsForAnyArgs(Result.Success());

        using HttpResponseMessage response = await ConsoleHost.PostFormAsync(_host.ClientAs(ConsoleHost.GlobalAdmin), "/", "/?handler=FinishOffboarding", new Dictionary<string, string> { ["userId"] = "u-eve" });

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await _host.Mail.Received(1).SetEnabledAsync("m-eve", false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_dashboard_never_offboards_an_active_user()
    {
        _host.AddUser("u-dan", "dan", "dan@acme.example");

        string html = await (await ConsoleHost.PostFormAsync(_host.ClientAs(ConsoleHost.GlobalAdmin), "/", "/?handler=FinishOffboarding", new Dictionary<string, string> { ["userId"] = "u-dan" })).Content.ReadAsStringAsync(Ct);

        html.ShouldContain("dan is not offboarded; nothing was changed.");
        await _host.Identities.DidNotReceiveWithAnyArgs().SetEnabledAsync(default!, default, Ct);
    }

    [Fact]
    public async Task Repairing_a_user_from_the_dashboard_creates_what_is_missing()
    {
        _host.AddUser("u-dan", "dan", "dan@acme.example");

        using HttpResponseMessage response = await ConsoleHost.PostFormAsync(_host.ClientAs(ConsoleHost.GlobalAdmin), "/", "/?handler=RepairUser", new Dictionary<string, string> { ["userId"] = "u-dan" });

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await _host.Mail.ReceivedWithAnyArgs(1).CreateAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task Global_admin_can_be_granted_only_to_someone_who_can_sign_in_and_is_not_the_page_lead()
    {
        _host.AddUser("u-dan", "dan", "dan@acme.example");
        (await GetAsync("/users/u-dan")).ShouldContain("<button type=\"submit\" name=\"grant\" value=\"true\" class=\"secondary\">");

        _host.AddUser("u-eve", "eve", "eve@acme.example", enabled: false);
        (await GetAsync("/users/u-eve")).ShouldNotContain("Make global administrator");
    }

    [Fact]
    public async Task A_refused_deletion_reads_as_a_refusal_with_a_way_back_not_as_an_outage()
    {
        _host.AddUser("u-eve", "eve", "eve@acme.example", enabled: false);
        _host.Mail.FindByAddressAsync("eve@acme.example", Arg.Any<CancellationToken>()).Returns(new MailAccount("m-eve", "eve@acme.example", true));

        string html = await GetAsync("/users/u-eve/delete/Mail");

        html.ShouldContain("Finish offboarding first");
        html.ShouldContain("<h1>Delete mail of eve</h1>");
        Crumbs(html).ShouldBe("Acme Pty › Users › eve › Delete mail of eve");
        html.ShouldContain("href=\"/users/u-eve\">Go back</a>");
        html.ShouldContain("action=\"/users/u-eve?handler=Offboard\"");
        html.ShouldNotContain("Could not read");
        html.ShouldNotContain("Try again");
    }

    [Fact]
    public async Task A_served_domain_without_dkim_keys_offers_repair_to_a_global_admin()
    {
        _host.MailDomains.FindAsync(ConsoleHost.Domain("acme.example"), Arg.Any<CancellationToken>()).Returns(new MailDomainInfo("d", ConsoleHost.Domain("acme.example"), false));

        (await GetAsync("/")).ShouldContain("handler=RepairDomain");
        (await GetAsync("/entities/" + ConsoleHost.Acme)).ShouldContain("handler=RepairDomain");
        (await GetAsync("/", ConsoleHost.EntityAdmin)).ShouldContain("Ask your Nightware operator to repair it.");
    }

    [Fact]
    public async Task A_served_domain_with_dkim_keys_offers_no_repair()
    {
        (await GetAsync("/entities/" + ConsoleHost.Acme)).ShouldNotContain("handler=RepairDomain");
    }

    [Fact]
    public async Task An_entity_admin_is_sent_to_the_operator_for_a_domain_the_mail_server_does_not_serve()
    {
        _host.MailDomains.FindAsync(ConsoleHost.Domain("acme.example"), Arg.Any<CancellationToken>()).Returns(Error.NotFound("Not on the mail server."));

        string html = await GetAsync("/", ConsoleHost.EntityAdmin);

        html.ShouldContain("Ask your Nightware operator to repair it.");
        html.ShouldNotContain("handler=RepairDomain");
    }

    [Fact]
    public async Task A_page_that_cannot_be_read_keeps_a_way_up_the_breadcrumb()
    {
        _host.Identities.GetAsync("u-dan", Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("Keycloak is down."));

        string html = await GetAsync("/users/u-dan");

        html.ShouldContain("<a href=\"/entities\">Entities</a> › <span aria-current=\"page\">User</span></nav>");
        html.ShouldContain("<h1>User</h1>");
        html.ShouldContain("Keycloak is down.");
    }

    [Fact]
    public async Task A_user_page_leads_with_the_action_their_status_calls_for()
    {
        _host.AddUser("u-dan", "dan", "dan@acme.example");
        string repair = await GetAsync("/users/u-dan");
        repair.ShouldContain("<button type=\"submit\">Repair</button>");
        repair.ShouldContain("class=\"danger quiet\">Offboard</button>");
        repair.ShouldContain("<details class=\"panel more\">");

        _host.AddUser("u-eve", "eve", "eve@acme.example", enabled: false);
        _host.Mail.FindByAddressAsync("eve@acme.example", Arg.Any<CancellationToken>()).Returns(new MailAccount("m-eve", "eve@acme.example", true));
        string incomplete = await GetAsync("/users/u-eve");
        incomplete.ShouldContain("<section class=\"panel incomplete\">");
        incomplete.ShouldContain("<button type=\"submit\">Finish offboarding</button>");
        incomplete.ShouldNotContain("class=\"warning\"");
    }

    [Fact]
    public async Task Signing_keys_read_as_a_state_and_addresses_without_brackets()
    {
        _host.MailDomains.FindAsync(ConsoleHost.Domain("acme.example"), Arg.Any<CancellationToken>()).Returns(new MailDomainInfo("d", ConsoleHost.Domain("acme.example"), false));
        _host.Entities.ListAdminsAsync(ConsoleHost.Acme, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<IdentityAccount>>.Success([new IdentityAccount("u-dan", "dan", "dan@acme.example", true)]));

        string html = await GetAsync("/entities/" + ConsoleHost.Acme);

        html.ShouldContain("<span class=\"chip missing\">Missing</span>");
        html.ShouldContain("<span class=\"hint\">dan@acme.example</span>");
        html.ShouldNotContain("&lt;dan@acme.example&gt;");
    }

    [Fact]
    public async Task The_delete_page_sits_under_the_users_entity_and_says_which_data()
    {
        _host.AddUser("u-dan", "dan", "dan@acme.example", enabled: false);

        string html = await GetAsync("/users/u-dan/delete/GroupwareData");

        Crumbs(html).ShouldBe("Acme Pty › Users › dan › Delete files &amp; calendar data");
        html.ShouldContain("<h1>Delete files &amp; calendar data of dan</h1>");
    }

    [Fact]
    public async Task A_failure_reads_as_a_plain_sentence_with_its_reason_beside_it()
    {
        _host.AddUser("u-eve", "eve", "eve@acme.example", enabled: false);
        _host.Mail.FindByAddressAsync("eve@acme.example", Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("Connection refused (mail.example:443)."));

        string global = await GetAsync("/users/u-eve");
        global.ShouldContain("Mail gave no answer for this account. <span class=\"hint detail\">Connection refused (mail.example:443).</span>");
        global.ShouldContain("it gave no answer, so it could not be shut.");
        global.ShouldNotContain("If this persists");

        (await GetAsync("/users/u-eve", ConsoleHost.EntityAdmin)).ShouldContain("If this persists, tell your Nightware operator.");
    }

    [Fact]
    public async Task An_entity_admins_dashboard_names_the_failure_plainly_and_who_to_tell()
    {
        _host.AddUser("u-eve", "eve", "eve@acme.example", enabled: false);
        _host.Mail.FindByAddressAsync("eve@acme.example", Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("Connection refused."));
        Members(new IdentityAccount("u-eve", "eve", "eve@acme.example", false));

        string html = await GetAsync("/", ConsoleHost.EntityAdmin);

        html.ShouldContain("Mail is not answering for eve. If this persists, tell your Nightware operator.");
        html.ShouldNotContain("answers its health check");
    }

    [Fact]
    public async Task Routine_details_do_not_repeat_the_backends_product_name()
    {
        _host.AddUser("u-dan", "dan", "dan@acme.example");

        string html = await GetAsync("/users/u-dan");

        html.ShouldContain("Signs in as dan.");
        html.ShouldContain("No account for dan.");
        html.ShouldNotContain("Nextcloud user");
        html.ShouldNotContain("Keycloak user");
    }
}

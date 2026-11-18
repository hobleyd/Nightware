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

    /// <summary>The left menu.</summary>
    private static string Side(string html) =>
        Regex.Match(html, "<nav class=\"side panel\"[^>]*>(.*?)</nav>", RegexOptions.Singleline).Groups[1].Value;

    /// <summary>The breadcrumb the left menu marks: the trail down to the current page, joined with ›.</summary>
    private static string Crumbs(string html) =>
        string.Join(" › ", Regex.Matches(Side(html), "<a (?:aria-current=\"page\"|class=\"trail\")[^>]*>(.*?)</a>").Select(m => m.Groups[1].Value));

    private async Task<string> GetAsync(string url, string sub = ConsoleHost.GlobalAdmin) =>
        await (await _host.ClientAs(sub).GetAsync(new Uri(url, UriKind.Relative), Ct)).Content.ReadAsStringAsync(Ct);

    private void Members(params IdentityAccount[] members) =>
        _host.Entities.ListMembersAsync(ConsoleHost.Acme, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<IdentityAccount>>.Success(members));

    [Fact]
    public async Task The_dashboard_and_sections_sit_in_a_left_menu()
    {
        string html = await GetAsync("/");

        string top = Regex.Match(html, "<header class=\"top\">(.*?)</header>", RegexOptions.Singleline).Groups[1].Value;
        top.ShouldNotContain(">Dashboard</a>");
        top.ShouldNotContain(">Entities</a>");
        string side = Side(html);
        side.ShouldContain("<a aria-current=\"page\" href=\"/\">Dashboard</a>");
        side.ShouldContain(">Entities</a>");
        side.ShouldContain(">Global admins</a>");
        html.ShouldContain("<h1 class=\"visually-hidden\">Dashboard</h1>");

        string entities = await GetAsync("/entities");
        entities.ShouldContain("<a aria-current=\"page\" href=\"/entities\">Entities</a>");
        entities.ShouldNotContain("aria-current=\"page\">Dashboard</a>");
    }

    [Fact]
    public async Task The_left_menu_lists_the_entities_and_marks_the_way_down_to_the_page()
    {
        _host.AddUser("u-dan", "dan", "dan@acme.example");

        string side = Side(await GetAsync("/users/u-dan"));

        side.ShouldContain("<a class=\"trail\" href=\"/entities/e-acme\">Acme Pty</a>");
        side.ShouldContain("<a href=\"/entities/e-other\">Other Co</a>");
        side.ShouldContain("<a class=\"trail\" href=\"/entities/e-acme/users\">Users</a>");
        side.ShouldContain("<a aria-current=\"page\" href=\"/users/u-dan\">dan</a>");
        Regex.Count(side, "aria-current=").ShouldBe(1);
        side.IndexOf(">Users</a>", StringComparison.Ordinal).ShouldBeLessThan(side.IndexOf(">Other Co</a>", StringComparison.Ordinal));

        Crumbs(await GetAsync("/entities/" + ConsoleHost.Acme)).ShouldBe("Entities › Acme Pty");
    }

    [Fact]
    public async Task An_entity_admins_menu_lists_only_their_own_entity()
    {
        string side = Side(await GetAsync("/entities", ConsoleHost.EntityAdmin));

        side.ShouldContain(">Acme Pty</a>");
        side.ShouldNotContain(ConsoleHost.Other);
        side.ShouldNotContain("Global admins");
    }

    [Fact]
    public async Task A_menu_whose_entities_cannot_be_read_still_leads_to_the_page()
    {
        _host.Entities.ListAsync(Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("Keycloak is down."));
        _host.AddUser("u-dan", "dan", "dan@acme.example");

        string html = await GetAsync("/users/u-dan");

        html.ShouldContain("<h1>dan <small>");
        Crumbs(html).ShouldBe("Entities › Entity › Users › dan");
        Side(html).ShouldContain("<a class=\"trail\" href=\"/entities/e-acme\">Entity</a>");
        Side(html).ShouldNotContain(ConsoleHost.Other);
    }

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

        Crumbs(html).ShouldBe("Entities › Acme Pty › DNS records for acme.example");
        Side(html).ShouldContain("<a class=\"trail\" href=\"/entities/e-acme\">Acme Pty</a>");
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

        html.ShouldContain("popovertarget=\"remove-spare-example\" title=\"Remove spare.example\"");
        html.ShouldNotContain("popovertarget=\"remove-acme-example\"");
        html.ShouldContain("<h3>Remove spare.example from Acme Pty?</h3>");
    }

    [Fact]
    public async Task Removing_a_domain_is_confirmed_in_a_popover_that_needs_no_script()
    {
        _host.Entities.GetAsync(ConsoleHost.Acme, Arg.Any<CancellationToken>()).Returns(new Entity(ConsoleHost.Acme, "acme", "Acme Pty", [ConsoleHost.Domain("acme.example"), ConsoleHost.Domain("spare.example")]));

        string html = await GetAsync("/entities/" + ConsoleHost.Acme);

        // The confirmation is the browser's popover, which needs no script; nothing posts from the row itself.
        html.ShouldNotContain("onsubmit");
        html.ShouldContain("<div id=\"remove-spare-example\" popover class=\"confirm\">");
        html.ShouldContain("<button type=\"button\" class=\"icon danger quiet\" popovertarget=\"remove-spare-example\"");
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
        html.ShouldContain("Mail gave no answer for this account. <span class=\"hint\">Connection refused.</span>");
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
        Crumbs(html).ShouldBe("Entities › Acme Pty › Users › eve › Delete mail of eve");
        html.ShouldContain("Finish offboarding first: their mailbox may still let them sign in.");
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

        Crumbs(html).ShouldBe("Entities › User");
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

        Crumbs(html).ShouldBe("Entities › Acme Pty › Users › dan › Delete files &amp; calendar data");
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

        string entityAdmin = await GetAsync("/users/u-eve", ConsoleHost.EntityAdmin);
        entityAdmin.ShouldContain("<p><strong>Mail is not answering. Tell your Nightware operator; once it answers, Finish offboarding shuts it.</strong></p>");
        entityAdmin.ShouldContain("<button type=\"submit\" class=\"secondary\">Finish offboarding</button>");
        entityAdmin.ShouldNotContain("run it again once the system is reachable");
        entityAdmin.IndexOf("Tell your Nightware operator", StringComparison.Ordinal).ShouldBeLessThan(entityAdmin.IndexOf(">Finish offboarding</button>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_entity_admins_dashboard_names_the_failure_plainly_and_who_to_tell()
    {
        _host.AddUser("u-eve", "eve", "eve@acme.example", enabled: false);
        _host.Mail.FindByAddressAsync("eve@acme.example", Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("Connection refused."));
        Members(new IdentityAccount("u-eve", "eve", "eve@acme.example", false));

        string html = await GetAsync("/", ConsoleHost.EntityAdmin);

        html.ShouldContain("Mail is not answering for eve. If this persists, tell your Nightware operator.");
        html.ShouldContain("Mail is not answering. Tell your Nightware operator; once it answers, Finish offboarding shuts it.");
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

    [Fact]
    public async Task A_users_page_is_reached_from_entities_and_a_global_admin_without_one_from_global_admins()
    {
        _host.AddUser("u-dan", "dan", "dan@acme.example");
        Crumbs(await GetAsync("/users/u-dan")).ShouldBe("Entities › Acme Pty › Users › dan");

        _host.Identities.GetAsync("u-ops", Arg.Any<CancellationToken>()).Returns(new IdentityAccount("u-ops", "ops", "ops@example.net", true));
        _host.Scopes.ResolveAsync("u-ops", Arg.Any<CancellationToken>()).Returns(new AdminScope(true, new HashSet<string>()));
        Crumbs(await GetAsync("/users/u-ops")).ShouldBe("Global admins › ops");
    }

    [Fact]
    public async Task An_entity_page_has_one_primary_action_at_most()
    {
        string html = await GetAsync("/entities/" + ConsoleHost.Acme);

        html.ShouldContain("<button type=\"submit\" class=\"secondary\">Add</button>");
        html.ShouldNotContain("<button type=\"submit\">Connect</button>");
    }

    [Fact]
    public async Task An_spf_that_lets_anyone_send_is_on_the_dashboard_with_the_way_to_fix_it()
    {
        _host.Entities.GetAsync(ConsoleHost.Acme, Arg.Any<CancellationToken>()).Returns(ConsoleHost.AcmeEntity with
        {
            MailPolicies = new Dictionary<MailDomain, DomainMailPolicy> { [ConsoleHost.Domain("acme.example")] = new(SpfRecord.Parse("v=spf1 +all").Value, null) },
        });

        string global = await GetAsync("/");
        global.ShouldContain("its SPF lets any server send mail as it");
        global.ShouldContain("?edit=true#mail-policy\">Fix SPF and DMARC</a>");
        global.ShouldNotContain("handler=RepairDomain");

        (await GetAsync("/", ConsoleHost.EntityAdmin)).ShouldContain("Ask your Nightware operator to fix it.");
    }

    [Fact]
    public async Task Global_admins_open_each_configured_system_admin_console_from_its_status()
    {
        _host.Settings["Admin:KeycloakAdminUrl"] = "https://vpn.example/auth/admin/master/console/#/nightware";
        _host.Settings["Admin:NextcloudAdminUrl"] = "https://cloud.example/settings/admin";

        string html = await GetAsync("/");

        html.ShouldContain("<a class=\"chip linked\" href=\"https://vpn.example/auth/admin/master/console/#/nightware\" target=\"_blank\" rel=\"noopener noreferrer\" title=\"Sign-in (Keycloak) admin console\">Sign-in: OK</a>");
        html.ShouldContain("<a class=\"chip linked\" href=\"https://cloud.example/settings/admin\" target=\"_blank\" rel=\"noopener noreferrer\" title=\"Files &amp; calendar (Nextcloud) admin console\">Files &amp; calendar: OK</a>");
        html.ShouldContain("<span class=\"chip linked\">Mail: OK</span>");
        html.ShouldNotContain("signs in with its own administrator account");
        (await GetAsync("/", ConsoleHost.EntityAdmin)).ShouldNotContain("https://vpn.example");
    }

    [Fact]
    public async Task Without_configured_admin_consoles_no_status_is_a_link() =>
        (await GetAsync("/")).ShouldNotContain("<a class=\"chip");

    [Fact]
    public async Task The_entities_table_links_each_domain_to_its_dns_page_and_counts_users()
    {
        Members(new IdentityAccount("u-amy", "amy", "amy@acme.example", true), new IdentityAccount("u-bob", "bob", "bob@acme.example", true));

        string html = await GetAsync("/");

        html.ShouldContain($"<a title=\"DNS, SPF and DMARC for acme.example\" href=\"{DnsUrl}\">acme.example</a>");
        html.ShouldContain("<td data-label=\"Users\">2</td>");
    }

    [Fact]
    public async Task An_entity_whose_users_cannot_be_read_shows_no_count()
    {
        _host.Entities.ListMembersAsync(ConsoleHost.Acme, Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("Keycloak is down."));

        (await GetAsync("/")).ShouldContain("<td data-label=\"Users\"><span title=\"Could not be read\">—</span></td>");
    }
}

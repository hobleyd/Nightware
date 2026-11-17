using System.Net;

using Nightware.Admin.Tests.Support;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Admin.Tests.Pages;

public sealed class DashboardAndReadFailureTests : IDisposable
{
    private readonly ConsoleHost _host = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string DnsUrl => $"/entities/{ConsoleHost.Acme}/dns/acme.example";

    public void Dispose() => _host.Dispose();

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
    }

    [Fact]
    public async Task Missing_dns_records_send_an_entity_admin_to_the_operator_not_to_a_button_they_lack()
    {
        _host.MailDomains.GetDnsRecordsAsync(default!, Ct).ReturnsForAnyArgs(Result<IReadOnlyList<DnsRecord>>.Success([]));

        (await GetAsync(DnsUrl)).ShouldContain("Use Repair on the entity page.");
        string html = await GetAsync(DnsUrl, ConsoleHost.EntityAdmin);
        html.ShouldContain("Ask your Nightware operator to repair the domain.");
        html.ShouldNotContain("Use Repair");
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

        html.ShouldContain("<option value=\"spare.example\">");
        html.ShouldNotContain("<option value=\"acme.example\">");
    }

    [Fact]
    public async Task When_every_domain_is_in_use_removal_says_why_instead_of_offering_a_form()
    {
        Members(new IdentityAccount("u-dan", "dan", "dan@acme.example", true));

        string html = await GetAsync("/entities/" + ConsoleHost.Acme);

        html.ShouldContain("Every domain still has users' addresses on it (acme.example: 1)");
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
        html.ShouldContain("href=\"/users/u-eve\">Go back</a>");
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
}

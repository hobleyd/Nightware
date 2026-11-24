using System.Net;

using Nightware.Admin.Tests.Support;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Admin.Tests.Pages;

/// <summary>An entity's shared mailboxes (phase 10): the list, a new one, one mailbox's page and deleting it.</summary>
public sealed class MailboxPagesTests : IDisposable
{
    private const string ListUrl = $"/entities/{ConsoleHost.Acme}/mailboxes";
    private const string NewUrl = $"/entities/{ConsoleHost.Acme}/mailboxes/new";
    private const string MailboxUrl = $"/entities/{ConsoleHost.Acme}/mailboxes/s-sup";
    private const string DeleteUrl = $"/entities/{ConsoleHost.Acme}/mailboxes/s-sup/delete";

    private readonly ConsoleHost _host = new();

    public void Dispose() => _host.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static MailAddress Address(string value) => MailAddress.TryParse(value, out MailAddress? a) ? a : throw new ArgumentException(value);

    private SharedMailbox GivenSupport()
    {
        var mailbox = new SharedMailbox("s-sup", ConsoleHost.Acme, Address("support@acme.example"), "Support") { Aliases = [Address("help@acme.example")] };
        _host.Mailboxes.GetAsync(ConsoleHost.Acme, "s-sup", Arg.Any<CancellationToken>()).Returns(mailbox);
        _host.Mailboxes.ListAsync(ConsoleHost.Acme, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<SharedMailbox>>.Success([mailbox]));
        return mailbox;
    }

    private async Task<(HttpStatusCode Status, string Html)> GetAsync(string url, string sub = ConsoleHost.GlobalAdmin)
    {
        using HttpResponseMessage response = await _host.ClientAs(sub).GetAsync(new Uri(url, UriKind.Relative), Ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private Task<HttpResponseMessage> PostAsync(string page, string handler, Dictionary<string, string> fields, string sub = ConsoleHost.GlobalAdmin) =>
        ConsoleHost.PostFormAsync(_host.ClientAs(sub), page, handler.Length == 0 ? page : $"{page}?handler={handler}", fields);

    [Fact]
    public async Task The_entity_menu_has_shared_mailboxes_and_the_list_offers_a_new_one()
    {
        (HttpStatusCode status, string html) = await GetAsync(ListUrl);

        status.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain("<h1>Shared mailboxes <small>Acme Pty</small></h1>");
        html.ShouldContain("None yet.");
        html.ShouldContain($"href=\"{NewUrl}\"");
        html.ShouldContain($"href=\"{ListUrl}\">Shared mailboxes</a>");
    }

    [Fact]
    public async Task The_list_names_each_mailbox_s_address()
    {
        GivenSupport();

        string html = (await GetAsync(ListUrl)).Html;

        html.ShouldContain($"href=\"{MailboxUrl}\">Support</a>");
        html.ShouldContain("support@acme.example");
    }

    [Fact]
    public async Task An_admin_of_another_entity_is_forbidden()
    {
        (await GetAsync($"/entities/{ConsoleHost.Other}/mailboxes", ConsoleHost.EntityAdmin)).Status.ShouldBe(HttpStatusCode.Forbidden);
        (await GetAsync($"/entities/{ConsoleHost.Other}/mailboxes/new", ConsoleHost.EntityAdmin)).Status.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Creating_a_shared_mailbox_builds_it_and_opens_it()
    {
        using HttpResponseMessage response = await PostAsync(NewUrl, string.Empty, new()
        {
            ["Input.Name"] = "Support",
            ["Input.LocalPart"] = "support",
            ["Input.Domain"] = "acme.example",
            ["Input.Aliases"] = "help@acme.example",
        }, ConsoleHost.EntityAdmin);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.ShouldBe($"/entities/{ConsoleHost.Acme}/mailboxes/s-new");
        await _host.Mailboxes.Received(1).CreateAsync(ConsoleHost.Acme, Arg.Is<NewSharedMailbox>(m =>
            m.Address.Value == "support@acme.example" && m.Name == "Support" && m.Aliases.Single().Value == "help@acme.example"), Arg.Any<CancellationToken>());
        await _host.SharedMail.Received(1).CreateAsync(Address("support@acme.example"), Arg.Any<IReadOnlyList<MailAddress>>(), "Support", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_refused_mailbox_is_explained_on_the_form()
    {
        using HttpResponseMessage response = await PostAsync(NewUrl, string.Empty, new()
        {
            ["Input.Name"] = "Support",
            ["Input.LocalPart"] = "postmaster",
            ["Input.Domain"] = "acme.example",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("reserved");
        await _host.Mailboxes.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task A_mailbox_page_shows_its_state_members_and_settings()
    {
        GivenSupport();
        IdentityAccount carol = _host.AddUser("u-carol", "carol", "carol@acme.example");
        _host.AddUser("u-dave", "dave", "dave@acme.example");
        _host.Entities.ListMembersAsync(ConsoleHost.Acme, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<IdentityAccount>>.Success(
            [carol, new IdentityAccount("u-dave", "dave", "dave@acme.example", true)]));
        _host.Mailboxes.ListMembersAsync(ConsoleHost.Acme, "s-sup", Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<IdentityAccount>>.Success([carol]));

        string html = (await GetAsync(MailboxUrl, ConsoleHost.EntityAdmin)).Html;

        html.ShouldContain("<h1>Support <small>Acme Pty</small></h1>");
        html.ShouldContain("Mailbox: ");
        html.ShouldContain("No shared mailbox at support@acme.example.");
        html.ShouldContain("handler=Repair");
        html.ShouldContain("(or help@acme.example)");
        html.ShouldContain("<td data-label=\"Address\">carol@acme.example</td>");
        html.ShouldContain("<option value=\"u-dave\">");
        html.ShouldNotContain("<option value=\"u-carol\">");
        html.ShouldContain("Address <strong>support@acme.example</strong>");
        // An entity admin may not delete data by default, so is not offered the delete page.
        html.ShouldNotContain(DeleteUrl);
        html.ShouldContain("only a global administrator can do it");
    }

    [Fact]
    public async Task A_mailbox_that_needs_repair_is_on_the_dashboard_and_repaired_from_there()
    {
        GivenSupport();

        string html = (await GetAsync("/", ConsoleHost.EntityAdmin)).Html;

        html.ShouldNotContain("Nothing needs you.");
        html.ShouldContain($"href=\"{MailboxUrl}\">Support</a> (support@acme.example, Acme Pty)");
        html.ShouldContain("Mail: Missing");
        html.ShouldContain("handler=RepairMailbox");

        using HttpResponseMessage response = await PostAsync("/", "RepairMailbox", new() { ["entityId"] = ConsoleHost.Acme, ["mailboxId"] = "s-sup" }, ConsoleHost.EntityAdmin);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await _host.SharedMail.ReceivedWithAnyArgs(1).CreateAsync(default!, default!, default!, Ct);
    }

    [Fact]
    public async Task Behind_the_console_prefix_the_dashboard_s_repair_posts_to_the_root_with_its_slash()
    {
        GivenSupport();
        using HttpClient client = _host.ClientAs(ConsoleHost.EntityAdmin);
        client.DefaultRequestHeaders.Add("X-Forwarded-Prefix", "/console");

        string html = await client.GetStringAsync(new Uri("/", UriKind.Relative), Ct);

        // The proxy redirects /console to /console/, and a browser follows that as a GET: the repair would be lost.
        string form = System.Text.RegularExpressions.Regex.Match(html, "<form[^>]*handler=RepairMailbox.*?</form>").Value;
        form.ShouldStartWith("<form method=\"post\" action=\"/console/?handler=RepairMailbox\"");
        form.ShouldContain("name=\"__RequestVerificationToken\"");
        html.ShouldNotContain("action=\"/console?");
    }

    [Fact]
    public async Task A_mailbox_a_system_failed_for_is_on_the_dashboard_and_the_systems_chip_names_it()
    {
        GivenSupport();
        _host.SharedCalendars.FindAsync(Address("support@acme.example"), Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("nextcloud:443 refused"));

        string html = (await GetAsync("/")).Html;

        html.ShouldContain("Mail: Missing; Files &amp; calendar gave no answer for it");
        html.ShouldContain("nextcloud:443 refused");
        html.ShouldContain("failed to answer for Support");
    }

    [Fact]
    public async Task The_calendar_panel_shows_its_state_and_who_is_waiting()
    {
        GivenSupport();
        IdentityAccount carol = _host.AddUser("u-carol", "carol", "carol@acme.example");
        _host.Mailboxes.ListMembersAsync(ConsoleHost.Acme, "s-sup", Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<IdentityAccount>>.Success([carol]));

        string missing = (await GetAsync(MailboxUrl)).Html;
        missing.ShouldContain("<h2>Calendar</h2>");
        missing.ShouldContain("Calendar: ");
        missing.ShouldContain("Not created yet");

        _host.SharedCalendars.FindAsync(Address("support@acme.example"), Arg.Any<CancellationToken>()).Returns(new SharedCalendarAccount("mbx-support@acme.example", "Support"));
        _host.GroupwareGroups.FindAsync("mbx-support@acme.example", Arg.Any<CancellationToken>()).Returns(new GroupwareGroup("mbx-support@acme.example", "Support", []));
        string waiting = (await GetAsync(MailboxUrl)).Html;
        waiting.ShouldContain("Ready");
        waiting.ShouldContain("waiting for carol to sign in to Nextcloud");
    }

    [Fact]
    public async Task Members_are_added_and_removed_from_the_mailbox_page()
    {
        GivenSupport();
        _host.AddUser("u-dave", "dave", "dave@acme.example");

        using HttpResponseMessage added = await PostAsync(MailboxUrl, "AddMember", new() { ["userId"] = "u-dave" }, ConsoleHost.EntityAdmin);
        using HttpResponseMessage removed = await PostAsync(MailboxUrl, "RemoveMember", new() { ["userId"] = "u-dave" }, ConsoleHost.EntityAdmin);

        added.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        removed.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await _host.Mailboxes.Received(1).AddMemberAsync(ConsoleHost.Acme, "s-sup", "u-dave", Arg.Any<CancellationToken>());
        await _host.Mailboxes.Received(1).RemoveMemberAsync(ConsoleHost.Acme, "s-sup", "u-dave", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Saving_keeps_the_address_and_changes_the_rest()
    {
        GivenSupport();

        using HttpResponseMessage response = await PostAsync(MailboxUrl, "Save", new()
        {
            ["Input.Name"] = "Help desk",
            ["Input.LocalPart"] = "other",
            ["Input.Aliases"] = "",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await _host.Mailboxes.Received(1).UpdateAsync(Arg.Is<SharedMailbox>(m => m.Name == "Help desk" && m.Address.Value == "support@acme.example" && m.Aliases.Count == 0), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Only_an_admin_who_may_delete_data_reaches_the_delete_page()
    {
        GivenSupport();

        (await GetAsync(MailboxUrl)).Html.ShouldContain($"href=\"{DeleteUrl}\"");
        (await GetAsync(DeleteUrl, ConsoleHost.EntityAdmin)).Status.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_stale_sign_in_is_sent_to_re_authenticate_before_the_delete_page()
    {
        GivenSupport();

        using HttpResponseMessage response = await _host.ClientAs(ConsoleHost.GlobalAdmin, authAge: TimeSpan.FromMinutes(10)).GetAsync(new Uri(DeleteUrl, UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.ShouldStartWith("/account/reauthenticate?returnUrl=");
    }

    [Fact]
    public async Task The_delete_page_shows_what_is_lost_and_deletes_only_with_the_address()
    {
        GivenSupport();
        _host.SharedMail.FindByAddressAsync(default!, Ct).ReturnsForAnyArgs(new SharedMailAccount("sm1", "support@acme.example"));
        _host.Mail.GetUsageAsync("sm1", Arg.Any<CancellationToken>()).Returns(Result<long>.Success(3 * 1048576));

        string page = (await GetAsync(DeleteUrl)).Html;
        page.ShouldContain("every message in it</strong> (3 MB)");
        page.ShouldContain("name=\"confirmation\"");

        using HttpResponseMessage wrong = await PostAsync(DeleteUrl, string.Empty, new() { ["confirmation"] = "Support" });
        wrong.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await wrong.Content.ReadAsStringAsync(Ct)).ShouldContain("does not match the shared mailbox");
        await _host.SharedMail.DidNotReceiveWithAnyArgs().DestroyAsync(default!, Ct);

        using HttpResponseMessage right = await PostAsync(DeleteUrl, string.Empty, new() { ["confirmation"] = "support@acme.example" });
        right.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        right.Headers.Location!.OriginalString.ShouldBe(ListUrl);
        await _host.SharedMail.Received(1).DestroyAsync("sm1", Arg.Any<CancellationToken>());
        await _host.Mailboxes.Received(1).DeleteAsync(ConsoleHost.Acme, "s-sup", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_user_s_page_lists_their_shared_mailboxes()
    {
        SharedMailbox mailbox = GivenSupport();
        _host.AddUser("u-carol", "carol", "carol@acme.example");
        _host.Mailboxes.ListUserMailboxesAsync(ConsoleHost.Acme, "u-carol", Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<SharedMailbox>>.Success([mailbox]));

        string html = (await GetAsync("/users/u-carol")).Html;

        html.ShouldContain("<h2>Shared mailboxes</h2>");
        html.ShouldContain($"href=\"{MailboxUrl}\">Support</a>");
    }
}

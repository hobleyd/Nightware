using System.Net;

using Nightware.Admin.Tests.Support;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Admin.Tests.Pages;

public sealed class UserPagesTests : IDisposable
{
    private readonly ConsoleHost _host = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewUserUrl => $"/entities/{ConsoleHost.Acme}/users/new";

    private static Dictionary<string, string> Carol(string delivery = "TemporaryPassword") => new()
    {
        ["Form.FirstName"] = "Carol",
        ["Form.LastName"] = "Jones",
        ["Form.LocalPart"] = "carol",
        ["Form.Domain"] = "acme.example",
        ["Form.Delivery"] = delivery,
    };

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task The_plan_step_shows_what_will_be_created_and_linked_without_writing()
    {
        _host.Mail.FindByAddressAsync("carol@acme.example", Arg.Any<CancellationToken>()).Returns(new MailAccount("m-carol", "carol@acme.example", true));

        using HttpResponseMessage response = await ConsoleHost.PostFormAsync(_host.ClientAs(ConsoleHost.EntityAdmin), NewUserUrl, NewUserUrl + "?handler=Plan", Carol());
        string html = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain("Will be created");
        html.ShouldContain("Will be linked");
        html.ShouldContain("Create and link");
        await _host.Identities.DidNotReceiveWithAnyArgs().CreateAsync(default!, Ct);
    }

    [Fact]
    public async Task A_conflict_hides_the_apply_button()
    {
        _host.Mail.FindByAddressAsync("carol@acme.example", Arg.Any<CancellationToken>()).Returns(new MailAccount("m-list", "carol@acme.example", false));

        string html = await (await ConsoleHost.PostFormAsync(_host.ClientAs(ConsoleHost.GlobalAdmin), NewUserUrl, NewUserUrl + "?handler=Plan", Carol())).Content.ReadAsStringAsync(Ct);

        html.ShouldContain("Conflict");
        html.ShouldNotContain("Create and link");
    }

    [Fact]
    public async Task Apply_shows_the_temporary_password_once_and_never_logs_it()
    {
        using HttpResponseMessage response = await ConsoleHost.PostFormAsync(_host.ClientAs(ConsoleHost.EntityAdmin), NewUserUrl, NewUserUrl + "?handler=Apply", Carol());
        string html = await response.Content.ReadAsStringAsync(Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        html.ShouldContain("shown once");
        // Razor encodes symbols such as '+' and '='; compare the decoded text.
        string password = WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(html, "<code>([^<]+)</code>").Groups[1].Value);
        password.Length.ShouldBe(20);
        await _host.Identities.Received(1).SetTemporaryPasswordAsync("u-new", Arg.Is<TemporaryCredential>(c => c.Password == password), Arg.Any<CancellationToken>());
        _host.Logs.Lines.ShouldContain(l => l.Contains("admin.audit user.provisioned by u-eadmin", StringComparison.Ordinal));
        _host.Logs.Lines.ShouldNotContain(l => l.Contains(password, StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_address_on_another_entitys_domain_is_refused()
    {
        Dictionary<string, string> form = Carol();
        form["Form.Domain"] = "other.example";

        string html = await (await ConsoleHost.PostFormAsync(_host.ClientAs(ConsoleHost.EntityAdmin), NewUserUrl, NewUserUrl + "?handler=Apply", form)).Content.ReadAsStringAsync(Ct);

        html.ShouldContain("other.example is not a domain of Acme Pty");
        await _host.Identities.DidNotReceiveWithAnyArgs().CreateAsync(default!, Ct);
    }

    [Fact]
    public async Task The_user_list_shows_link_chips()
    {
        _host.Entities.ListMembersAsync(ConsoleHost.Acme, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<IdentityAccount>>.Success([new IdentityAccount("u-dan", "dan", "dan@acme.example", true)]));

        string html = await (await _host.ClientAs(ConsoleHost.EntityAdmin).GetAsync(new Uri($"/entities/{ConsoleHost.Acme}/users", UriKind.Relative), Ct)).Content.ReadAsStringAsync(Ct);

        html.ShouldContain("dan@acme.example");
        html.ShouldContain("Sign-in: Linked");
        html.ShouldContain("Mail: Missing");
    }

    [Fact]
    public async Task Offboarding_needs_the_confirmation_box()
    {
        _host.AddUser("u-dan", "dan", "dan@acme.example");
        HttpClient client = _host.ClientAs(ConsoleHost.EntityAdmin);

        string html = await (await ConsoleHost.PostFormAsync(client, "/users/u-dan", "/users/u-dan?handler=Offboard", new Dictionary<string, string>())).Content.ReadAsStringAsync(Ct);
        html.ShouldContain("Tick the box");
        await _host.Identities.DidNotReceiveWithAnyArgs().SetEnabledAsync(default!, default, Ct);

        using HttpResponseMessage done = await ConsoleHost.PostFormAsync(client, "/users/u-dan", "/users/u-dan?handler=Offboard", new Dictionary<string, string> { ["confirm"] = "true" });
        done.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await _host.Identities.Received(1).SetEnabledAsync("u-dan", false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Finding_one_user_opens_them_and_several_are_listed_with_their_entity()
    {
        _host.AddUser("u-dan", "dan", "dan@acme.example");
        _host.Entities.ListMembersAsync(ConsoleHost.Acme, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<IdentityAccount>>.Success(
        [
            new IdentityAccount("u-dan", "dan", "dan@acme.example", true),
            new IdentityAccount("u-dana", "dana", "dana@acme.example", true),
        ]));
        HttpClient client = _host.ClientAs(ConsoleHost.GlobalAdmin);

        using HttpResponseMessage one = await client.GetAsync(new Uri("/users/find?q=dan%40", UriKind.Relative), Ct);
        one.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        one.Headers.Location!.OriginalString.ShouldBe("/users/u-dan");

        string several = await (await client.GetAsync(new Uri("/users/find?q=dan", UriKind.Relative), Ct)).Content.ReadAsStringAsync(Ct);
        several.ShouldContain("dana@acme.example");
        several.ShouldContain(">Acme Pty</a>");
    }

    [Fact]
    public async Task The_user_page_names_their_entity_and_puts_access_before_password()
    {
        _host.AddUser("u-dan", "dan", "dan@acme.example");

        string html = await (await _host.ClientAs(ConsoleHost.GlobalAdmin).GetAsync(new Uri("/users/u-dan", UriKind.Relative), Ct)).Content.ReadAsStringAsync(Ct);

        html.ShouldContain("aria-label=\"Breadcrumb\"");
        html.IndexOf("<h2>Access</h2>", StringComparison.Ordinal).ShouldBeLessThan(html.IndexOf("<h2>Password</h2>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_offboard_box_is_required_in_the_form()
    {
        _host.AddUser("u-dan", "dan", "dan@acme.example");

        string html = await (await _host.ClientAs(ConsoleHost.GlobalAdmin).GetAsync(new Uri("/users/u-dan", UriKind.Relative), Ct)).Content.ReadAsStringAsync(Ct);

        html.ShouldContain("<input type=\"checkbox\" name=\"confirm\" value=\"true\" required> Offboard dan");
    }

    [Fact]
    public async Task A_disabled_user_whose_mailbox_still_signs_in_shows_offboarding_incomplete()
    {
        _host.AddUser("u-dan", "dan", "dan@acme.example", enabled: false);
        _host.Mail.FindByAddressAsync("dan@acme.example", Arg.Any<CancellationToken>()).Returns(new MailAccount("m-dan", "dan@acme.example", true));

        string html = await (await _host.ClientAs(ConsoleHost.GlobalAdmin).GetAsync(new Uri("/users/u-dan", UriKind.Relative), Ct)).Content.ReadAsStringAsync(Ct);

        html.ShouldContain("Offboarding incomplete.");
        html.ShouldContain("Finish offboarding");
        html.ShouldNotContain("sign-in is disabled in every system");
    }

    [Fact]
    public async Task A_completed_offboarding_says_so_and_offers_no_finish()
    {
        _host.AddUser("u-dan", "dan", "dan@acme.example", enabled: false);

        string html = await (await _host.ClientAs(ConsoleHost.GlobalAdmin).GetAsync(new Uri("/users/u-dan", UriKind.Relative), Ct)).Content.ReadAsStringAsync(Ct);

        html.ShouldContain("sign-in is disabled in every system");
        html.ShouldNotContain("Finish offboarding");
    }

    [Fact]
    public async Task Granting_global_admin_needs_the_confirmation_box()
    {
        _host.AddUser("u-dan", "dan", "dan@acme.example");
        HttpClient client = _host.ClientAs(ConsoleHost.GlobalAdmin);

        string html = await (await ConsoleHost.PostFormAsync(client, "/users/u-dan", "/users/u-dan?handler=GlobalAdmin", new Dictionary<string, string> { ["grant"] = "true" })).Content.ReadAsStringAsync(Ct);

        html.ShouldContain("Tick the box to confirm the global administrator rights.");
        await _host.Identities.DidNotReceiveWithAnyArgs().SetGlobalAdminAsync(default!, default, Ct);
    }

    [Fact]
    public async Task Granting_global_admin_with_a_stale_sign_in_returns_to_the_user_page_after_signing_in_again()
    {
        _host.AddUser("u-dan", "dan", "dan@acme.example");
        HttpClient client = _host.ClientAs(ConsoleHost.GlobalAdmin, authAge: TimeSpan.FromMinutes(10));

        using HttpResponseMessage response = await ConsoleHost.PostFormAsync(client, "/users/u-dan", "/users/u-dan?handler=GlobalAdmin", new Dictionary<string, string> { ["grant"] = "true", ["confirm"] = "true" });

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.ShouldBe("/account/reauthenticate?returnUrl=%2Fusers%2Fu-dan");
        await _host.Identities.DidNotReceiveWithAnyArgs().SetGlobalAdminAsync(default!, default, Ct);
    }

    [Fact]
    public async Task The_danger_zone_is_global_only_and_only_for_disabled_users()
    {
        _host.AddUser("u-dan", "dan", "dan@acme.example", enabled: false);

        (await (await _host.ClientAs(ConsoleHost.GlobalAdmin).GetAsync(new Uri("/users/u-dan", UriKind.Relative), Ct)).Content.ReadAsStringAsync(Ct)).ShouldContain("Danger zone");
        (await (await _host.ClientAs(ConsoleHost.EntityAdmin).GetAsync(new Uri("/users/u-dan", UriKind.Relative), Ct)).Content.ReadAsStringAsync(Ct)).ShouldNotContain("Danger zone");

        _host.AddUser("u-dan", "dan", "dan@acme.example", enabled: true);
        (await (await _host.ClientAs(ConsoleHost.GlobalAdmin).GetAsync(new Uri("/users/u-dan", UriKind.Relative), Ct)).Content.ReadAsStringAsync(Ct)).ShouldNotContain("Danger zone");
    }

    [Fact]
    public async Task A_stale_sign_in_is_sent_to_re_authenticate_before_the_delete_page()
    {
        _host.AddUser("u-dan", "dan", "dan@acme.example", enabled: false);

        using HttpResponseMessage response = await _host.ClientAs(ConsoleHost.GlobalAdmin, authAge: TimeSpan.FromMinutes(10)).GetAsync(new Uri("/users/u-dan/delete/Mail", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.ShouldBe("/account/reauthenticate?returnUrl=%2Fusers%2Fu-dan%2Fdelete%2FMail");
    }

    [Fact]
    public async Task A_weak_sign_in_is_sent_to_re_authenticate()
    {
        _host.AddUser("u-dan", "dan", "dan@acme.example", enabled: false);

        using HttpResponseMessage response = await _host.ClientAs(ConsoleHost.GlobalAdmin, acr: "pwd").GetAsync(new Uri("/users/u-dan/delete/Mail", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task Deleting_mail_needs_the_retyped_address_and_never_happens_on_get()
    {
        _host.AddUser("u-dan", "dan", "dan@acme.example", enabled: false);
        _host.Mail.FindByAddressAsync("dan@acme.example", Arg.Any<CancellationToken>()).Returns(new MailAccount("m-dan", "dan@acme.example", true));
        HttpClient client = _host.ClientAs(ConsoleHost.GlobalAdmin);
        const string url = "/users/u-dan/delete/Mail";

        string preview = await (await client.GetAsync(new Uri(url, UriKind.Relative), Ct)).Content.ReadAsStringAsync(Ct);
        preview.ShouldContain("5 MB of stored mail");
        preview.ShouldContain("Nextcloud files, calendars, contacts and tasks");

        string wrong = await (await ConsoleHost.PostFormAsync(client, url, url, new Dictionary<string, string> { ["confirmation"] = "dan@acme.exampl" })).Content.ReadAsStringAsync(Ct);
        wrong.ShouldContain("does not match");
        await _host.Mail.DidNotReceiveWithAnyArgs().DestroyAsync(default!, Ct);

        using HttpResponseMessage done = await ConsoleHost.PostFormAsync(client, url, url, new Dictionary<string, string> { ["confirmation"] = "dan@acme.example" });
        done.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await _host.Mail.Received(1).DestroyAsync("m-dan", Arg.Any<CancellationToken>());
        _host.Logs.Lines.ShouldContain(l => l.Contains("admin.audit user.mail.deleted by u-global succeeded=True", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_forged_delete_post_by_an_entity_admin_is_refused()
    {
        _host.AddUser("u-dan", "dan", "dan@acme.example", enabled: false);
        _host.Mail.FindByAddressAsync("dan@acme.example", Arg.Any<CancellationToken>()).Returns(new MailAccount("m-dan", "dan@acme.example", true));
        HttpClient client = _host.ClientAs(ConsoleHost.EntityAdmin);

        // The entity admin takes a token from a page they can open, then posts to the delete page.
        using HttpResponseMessage response = await ConsoleHost.PostFormAsync(client, "/users/u-dan", "/users/u-dan/delete/Mail", new Dictionary<string, string> { ["confirmation"] = "dan@acme.example" });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await _host.Mail.DidNotReceiveWithAnyArgs().DestroyAsync(default!, Ct);
    }
}

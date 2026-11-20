using System.Net;

using Nightware.Admin.Tests.Support;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Admin.Tests.Pages;

/// <summary>Who receives each domain's mail to unknown addresses: chosen on the entity page by its admins.</summary>
public sealed class CatchAllPageTests : IDisposable
{
    private const string Alice = "alice@acme.example";

    private readonly ConsoleHost _host = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string EntityUrl => $"/entities/{ConsoleHost.Acme}";

    private static MailDomain Acme => ConsoleHost.Domain("acme.example");

    public CatchAllPageTests()
    {
        IdentityAccount alice = _host.AddUser("u-alice", "alice", Alice);
        IdentityAccount dan = _host.AddUser("u-dan", "dan", "dan@acme.example");
        _host.Entities.ListMembersAsync(ConsoleHost.Acme, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<IdentityAccount>>.Success([alice, dan]));
        _host.Mail.FindByAddressAsync(Alice, Arg.Any<CancellationToken>()).Returns(new MailAccount("m-alice", Alice, true));
        _host.Entities.SetCatchAllAsync(default!, default!, default, Ct).ReturnsForAnyArgs(ConsoleHost.AcmeEntity);
        _host.MailDomains.SetCatchAllAsync(default!, default, Ct).ReturnsForAnyArgs(Result.Success());
    }

    public void Dispose() => _host.Dispose();

    private async Task<string> GetAsync(string sub = ConsoleHost.GlobalAdmin) =>
        await (await _host.ClientAs(sub).GetAsync(new Uri(EntityUrl, UriKind.Relative), Ct)).Content.ReadAsStringAsync(Ct);

    private void GivenCatchAll(string userId, string? onServer) =>
        GivenCatchAll(ConsoleHost.AcmeEntity with { CatchAlls = new Dictionary<MailDomain, string> { [Acme] = userId } }, onServer);

    private void GivenCatchAll(Entity entity, string? onServer)
    {
        _host.Entities.GetAsync(ConsoleHost.Acme, Arg.Any<CancellationToken>()).Returns(entity);
        _host.MailDomains.FindAsync(Acme, Arg.Any<CancellationToken>()).Returns(new MailDomainInfo("d", Acme, true) { CatchAllAddress = onServer });
    }

    [Fact]
    public async Task An_entity_admin_is_offered_the_users_who_can_receive_it()
    {
        string html = await GetAsync(ConsoleHost.EntityAdmin);

        html.ShouldContain("<th>Fallback</th>");
        html.ShouldContain("<option value=\"\">No one: refuse it</option>");
        html.ShouldContain($"<option value=\"u-alice\">{Alice}</option>");
        // dan has no mailbox of his own yet, so the mail would bounce.
        html.ShouldNotContain("<option value=\"u-dan\"");
        html.ShouldContain("handler=CatchAll");
    }

    [Fact]
    public async Task The_chosen_user_is_selected_and_nothing_is_flagged_while_the_mail_server_agrees()
    {
        GivenCatchAll("u-alice", "ALICE@acme.example");

        string html = await GetAsync();

        html.ShouldContain($"<option value=\"u-alice\" selected=\"selected\">{Alice}</option>");
        html.ShouldNotContain("<p class=\"warning\">");
    }

    [Theory]
    [InlineData("u-alice", null, "still refuses it rather than sending it to alice@acme.example. Save again.")]
    [InlineData("u-alice", "bob@acme.example", "sends it to bob@acme.example, not alice@acme.example. Save again.")]
    [InlineData("u-gone", Alice, "a user who is no longer one of this entity&#x27;s.")]
    public async Task A_catch_all_out_of_step_with_the_mail_server_is_flagged(string userId, string? onServer, string flagged)
    {
        GivenCatchAll(userId, onServer);

        (await GetAsync()).ShouldContain(flagged);
    }

    [Fact]
    public async Task One_set_outside_the_console_or_held_by_an_offboarded_user_is_flagged()
    {
        GivenCatchAll(ConsoleHost.AcmeEntity, "postmaster@acme.example");
        (await GetAsync()).ShouldContain("sends it to postmaster@acme.example, set outside the console.");

        IdentityAccount erin = _host.AddUser("u-erin", "erin", "erin@acme.example", enabled: false);
        _host.Entities.ListMembersAsync(ConsoleHost.Acme, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<IdentityAccount>>.Success([erin]));
        GivenCatchAll("u-erin", "erin@acme.example");
        string html = await GetAsync();
        html.ShouldContain("erin is offboarded, so no one reads this mail.");
        // Still shown as the current choice although no longer offered.
        html.ShouldContain("<option value=\"u-erin\" selected>erin@acme.example</option>");
    }

    [Fact]
    public async Task Saving_sets_it_and_returns_to_the_page_naming_the_address()
    {
        HttpClient client = _host.ClientAs(ConsoleHost.EntityAdmin);
        using HttpResponseMessage response = await ConsoleHost.PostFormAsync(client, EntityUrl, $"{EntityUrl}?handler=CatchAll", new Dictionary<string, string> { ["domain"] = "acme.example", ["userId"] = "u-alice" });

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await _host.Entities.Received(1).SetCatchAllAsync(ConsoleHost.Acme, Acme, "u-alice", Arg.Any<CancellationToken>());
        await _host.MailDomains.Received(1).SetCatchAllAsync(Acme, Arg.Is<MailAddress?>(a => a!.Value == Alice), Arg.Any<CancellationToken>());
        (await (await client.GetAsync(response.Headers.Location, Ct)).Content.ReadAsStringAsync(Ct)).ShouldContain($"Mail to unknown addresses on acme.example now goes to {Alice}.");
    }

    [Fact]
    public async Task A_refused_choice_is_shown_on_the_page()
    {
        using HttpResponseMessage response = await ConsoleHost.PostFormAsync(_host.ClientAs(ConsoleHost.GlobalAdmin), EntityUrl, $"{EntityUrl}?handler=CatchAll", new Dictionary<string, string> { ["domain"] = "acme.example", ["userId"] = "u-dan" });

        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("dan has no mailbox of their own on the mail server yet");
        await _host.Entities.DidNotReceiveWithAnyArgs().SetCatchAllAsync(default!, default!, default, Ct);
    }
}

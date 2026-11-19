using System.Net;

using Nightware.Admin.Tests.Support;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

using Group = Nightware.Domain.Administration.Group;

namespace Nightware.Admin.Tests.Pages;

/// <summary>An entity's groups (phase 9): the list, a new group, one group's page and deleting its team folder.</summary>
public sealed class GroupPagesTests : IDisposable
{
    private const string ListUrl = $"/entities/{ConsoleHost.Acme}/groups";
    private const string NewUrl = $"/entities/{ConsoleHost.Acme}/groups/new";
    private const string GroupUrl = $"/entities/{ConsoleHost.Acme}/groups/g-fin";
    private const string FolderUrl = $"/entities/{ConsoleHost.Acme}/groups/g-fin/delete-folder";

    private readonly ConsoleHost _host = new();

    public void Dispose() => _host.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static GroupKey Key(string value) => GroupKey.TryParse(value, out GroupKey? key) ? key : throw new ArgumentException(value);

    private static MailAddress Address(string value) => MailAddress.TryParse(value, out MailAddress? a) ? a : throw new ArgumentException(value);

    private Group GivenFinance(bool teamFolder = false)
    {
        var group = new Group("g-fin", ConsoleHost.Acme, Key("finance"), "Finance")
        {
            Mail = new GroupMail(Address("finance@acme.example"), SenderPolicy.Members),
            Files = new GroupFiles(teamFolder) { TeamFolderId = teamFolder ? 3 : null },
        };
        _host.Groups.GetAsync(ConsoleHost.Acme, "g-fin", Arg.Any<CancellationToken>()).Returns(group);
        _host.Groups.ListAsync(ConsoleHost.Acme, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<Group>>.Success([group]));
        return group;
    }

    private async Task<(HttpStatusCode Status, string Html)> GetAsync(string url, string sub = ConsoleHost.GlobalAdmin)
    {
        using HttpResponseMessage response = await _host.ClientAs(sub).GetAsync(new Uri(url, UriKind.Relative), Ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private Task<HttpResponseMessage> PostAsync(string page, string handler, Dictionary<string, string> fields, string sub = ConsoleHost.GlobalAdmin) =>
        ConsoleHost.PostFormAsync(_host.ClientAs(sub), page, handler.Length == 0 ? page : $"{page}?handler={handler}", fields);

    [Fact]
    public async Task The_entity_menu_has_groups_and_the_list_offers_a_new_one()
    {
        (HttpStatusCode status, string html) = await GetAsync(ListUrl);

        status.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain("<h1>Groups <small>Acme Pty</small></h1>");
        html.ShouldContain("None yet.");
        html.ShouldContain($"href=\"{NewUrl}\"");
        html.ShouldContain($"href=\"{ListUrl}\">Groups</a>");
    }

    [Fact]
    public async Task The_list_names_each_group_s_address_and_files()
    {
        GivenFinance(teamFolder: true);

        string html = (await GetAsync(ListUrl)).Html;

        html.ShouldContain($"href=\"{GroupUrl}\">Finance</a>");
        html.ShouldContain("finance@acme.example");
        html.ShouldContain("Group and team folder");
    }

    [Fact]
    public async Task An_admin_of_another_entity_is_forbidden()
    {
        (await GetAsync($"/entities/{ConsoleHost.Other}/groups", ConsoleHost.EntityAdmin)).Status.ShouldBe(HttpStatusCode.Forbidden);
        (await GetAsync($"/entities/{ConsoleHost.Other}/groups/new", ConsoleHost.EntityAdmin)).Status.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Creating_a_group_builds_it_and_opens_it()
    {
        using HttpResponseMessage response = await PostAsync(NewUrl, string.Empty, new()
        {
            ["Input.Name"] = "Finance",
            ["Input.Mail"] = "true",
            ["Input.LocalPart"] = "finance",
            ["Input.Domain"] = "acme.example",
            ["Input.Senders"] = "Organization",
            ["Input.ExternalRecipients"] = "accountant@example.net",
            ["Input.Files"] = "true",
        }, ConsoleHost.EntityAdmin);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.ShouldBe($"/entities/{ConsoleHost.Acme}/groups/g-new");
        await _host.Groups.Received(1).CreateAsync(ConsoleHost.Acme, Arg.Is<NewGroup>(g =>
            g.Key.Value == "finance" && g.Mail!.Senders == SenderPolicy.Organization && g.Mail.ExternalRecipients.Single().Value == "accountant@example.net" && g.Files == new GroupFiles(false)), Arg.Any<CancellationToken>());
        await _host.Lists.ReceivedWithAnyArgs(1).CreateAsync(default!, default!, default!, default!, Ct);
        await _host.GroupwareGroups.Received(1).CreateAsync("acme-finance", "Finance", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_refused_group_is_explained_on_the_form()
    {
        using HttpResponseMessage response = await PostAsync(NewUrl, string.Empty, new()
        {
            ["Input.Name"] = "Finance",
            ["Input.Mail"] = "true",
            ["Input.LocalPart"] = "finance",
            ["Input.Domain"] = "acme.example",
            ["Input.Senders"] = "Anyone",
            ["Input.ExternalRecipients"] = "accountant@example.net",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldContain("cannot accept mail from anyone");
        await _host.Groups.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task A_group_page_shows_its_state_members_and_settings()
    {
        GivenFinance();
        IdentityAccount carol = _host.AddUser("u-carol", "carol", "carol@acme.example");
        _host.AddUser("u-dave", "dave", "dave@acme.example");
        _host.Entities.ListMembersAsync(ConsoleHost.Acme, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<IdentityAccount>>.Success(
            [carol, new IdentityAccount("u-dave", "dave", "dave@acme.example", true)]));
        _host.Groups.ListMembersAsync(ConsoleHost.Acme, "g-fin", Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<IdentityAccount>>.Success([carol]));

        string html = (await GetAsync(GroupUrl, ConsoleHost.EntityAdmin)).Html;

        html.ShouldContain("<h1>Finance <small>Acme Pty</small></h1>");
        html.ShouldContain("List: ");
        html.ShouldContain("No list at finance@acme.example.");
        html.ShouldContain("handler=Repair");
        html.ShouldContain("Only members may send to it.");
        html.ShouldContain("<td data-label=\"Address\">carol@acme.example</td>");
        html.ShouldContain("<option value=\"u-dave\">");
        html.ShouldNotContain("<option value=\"u-carol\">");
        html.ShouldContain("Address <strong>finance@acme.example</strong>");
    }

    [Fact]
    public async Task Members_are_added_and_removed_from_the_group_page()
    {
        GivenFinance();
        _host.AddUser("u-dave", "dave", "dave@acme.example");

        using HttpResponseMessage added = await PostAsync(GroupUrl, "AddMember", new() { ["userId"] = "u-dave" }, ConsoleHost.EntityAdmin);
        using HttpResponseMessage removed = await PostAsync(GroupUrl, "RemoveMember", new() { ["userId"] = "u-dave" }, ConsoleHost.EntityAdmin);

        added.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        removed.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await _host.Groups.Received(1).AddMemberAsync(ConsoleHost.Acme, "g-fin", "u-dave", Arg.Any<CancellationToken>());
        await _host.Groups.Received(1).RemoveMemberAsync(ConsoleHost.Acme, "g-fin", "u-dave", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Saving_keeps_the_address_and_changes_the_rest()
    {
        GivenFinance();

        using HttpResponseMessage response = await PostAsync(GroupUrl, "Save", new()
        {
            ["Input.Name"] = "Money",
            ["Input.Mail"] = "true",
            ["Input.LocalPart"] = "other",
            ["Input.Aliases"] = "accounts@acme.example",
            ["Input.Senders"] = "Members",
            ["Input.Files"] = "true",
        });

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await _host.Groups.Received(1).UpdateAsync(Arg.Is<Group>(g => g.Name == "Money" && g.Mail!.Address.Value == "finance@acme.example" && g.Mail.Aliases.Single().Value == "accounts@acme.example"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Deleting_a_group_needs_the_box_ticked()
    {
        GivenFinance();

        using HttpResponseMessage unconfirmed = await PostAsync(GroupUrl, "Delete", []);
        unconfirmed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await unconfirmed.Content.ReadAsStringAsync(Ct)).ShouldContain("Tick the box to confirm deleting the group.");
        await _host.Groups.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default!, Ct);

        using HttpResponseMessage confirmed = await PostAsync(GroupUrl, "Delete", new() { ["confirm"] = "true" });
        confirmed.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        confirmed.Headers.Location!.OriginalString.ShouldBe(ListUrl);
        await _host.Groups.Received(1).DeleteAsync(ConsoleHost.Acme, "g-fin", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Only_a_global_admin_is_offered_to_delete_the_team_folder()
    {
        GivenFinance(teamFolder: true);

        (await GetAsync(GroupUrl)).Html.ShouldContain($"href=\"{FolderUrl}\"");
        (await GetAsync(GroupUrl, ConsoleHost.EntityAdmin)).Html.ShouldNotContain(FolderUrl);
        (await GetAsync(FolderUrl, ConsoleHost.EntityAdmin)).Status.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_stale_sign_in_is_sent_to_re_authenticate_before_the_folder_page()
    {
        GivenFinance(teamFolder: true);

        using HttpResponseMessage response = await _host.ClientAs(ConsoleHost.GlobalAdmin, authAge: TimeSpan.FromMinutes(10)).GetAsync(new Uri(FolderUrl, UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.ShouldStartWith("/account/reauthenticate?returnUrl=");
    }

    [Fact]
    public async Task The_team_folder_is_deleted_only_with_the_group_s_name()
    {
        GivenFinance(teamFolder: true);

        using HttpResponseMessage wrong = await PostAsync(FolderUrl, string.Empty, new() { ["confirmation"] = "Sales" });
        wrong.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await wrong.Content.ReadAsStringAsync(Ct)).ShouldContain("does not match the group");
        await _host.GroupwareGroups.DidNotReceiveWithAnyArgs().DeleteFolderAsync(default, Ct);

        using HttpResponseMessage right = await PostAsync(FolderUrl, string.Empty, new() { ["confirmation"] = "Finance" });
        right.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        await _host.GroupwareGroups.Received(1).DeleteFolderAsync(3, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_user_s_page_lists_their_groups()
    {
        Group group = GivenFinance();
        _host.AddUser("u-carol", "carol", "carol@acme.example");
        _host.Groups.ListUserGroupsAsync(ConsoleHost.Acme, "u-carol", Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<Group>>.Success([group]));

        string html = (await GetAsync("/users/u-carol")).Html;

        html.ShouldContain("<h2>Groups</h2>");
        html.ShouldContain($"href=\"{GroupUrl}\">Finance</a>");
    }
}

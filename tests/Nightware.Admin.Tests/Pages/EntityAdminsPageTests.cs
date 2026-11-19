using System.Net;

using Nightware.Admin.Tests.Support;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Admin.Tests.Pages;

/// <summary>An entity's administrators, on their own page rather than the entity's.</summary>
public sealed class EntityAdminsPageTests : IDisposable
{
    private const string AdminsUrl = "/entities/" + ConsoleHost.Acme + "/admins";

    private readonly ConsoleHost _host = new();

    public EntityAdminsPageTests()
    {
        IdentityAccount dan = _host.AddUser("u-dan", "dan", "dan@acme.example");
        IdentityAccount erin = _host.AddUser("u-erin", "erin", "erin@acme.example");
        _host.Entities.ListMembersAsync(ConsoleHost.Acme, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<IdentityAccount>>.Success([dan, erin]));
        _host.Entities.ListAdminsAsync(ConsoleHost.Acme, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<IdentityAccount>>.Success([erin]));
        _host.Entities.GrantAdminAsync(default!, default!, Ct).ReturnsForAnyArgs(Result.Success());
        _host.Identities.AddRequiredActionAsync(default!, default, Ct).ReturnsForAnyArgs(Result.Success());
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    private async Task<string> GetAsync(string url) =>
        await (await _host.ClientAs(ConsoleHost.GlobalAdmin).GetAsync(new Uri(url, UriKind.Relative), Ct)).Content.ReadAsStringAsync(Ct);

    private Task<HttpResponseMessage> PostAsync(string handler, Dictionary<string, string> fields) =>
        ConsoleHost.PostFormAsync(_host.ClientAs(ConsoleHost.GlobalAdmin), AdminsUrl, $"{AdminsUrl}?handler={handler}", fields);

    [Fact]
    public async Task The_page_lists_the_administrators_and_offers_the_other_members()
    {
        string html = await GetAsync(AdminsUrl);

        html.ShouldContain("<h1>Administrators <small>Acme Pty</small></h1>");
        html.ShouldContain("<a href=\"/users/u-erin\">erin</a>");
        html.ShouldContain("<option value=\"u-dan\">dan (dan@acme.example)</option>");
        // erin is offered only for removal, not again as a new administrator.
        html.Split("<option value=\"u-erin\">").Length.ShouldBe(2);
        html.ShouldContain("handler=RevokeAdmin");
    }

    [Fact]
    public async Task Granting_needs_the_box_and_then_returns_to_the_page()
    {
        string unconfirmed = await (await PostAsync("GrantAdmin", new() { ["userId"] = "u-dan" })).Content.ReadAsStringAsync(Ct);
        unconfirmed.ShouldContain("Tick the box to confirm the new administrator.");
        await _host.Entities.DidNotReceiveWithAnyArgs().GrantAdminAsync(default!, default!, Ct);

        using HttpResponseMessage response = await PostAsync("GrantAdmin", new() { ["userId"] = "u-dan", ["confirm"] = "true" });

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.ShouldBe(AdminsUrl);
        await _host.Entities.Received(1).GrantAdminAsync(ConsoleHost.Acme, "u-dan", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_entity_page_no_longer_carries_the_administrators()
    {
        string html = await GetAsync("/entities/" + ConsoleHost.Acme);

        html.ShouldNotContain("Entity administrators");
        html.ShouldNotContain("handler=GrantAdmin");
        html.ShouldNotContain("handler=RevokeAdmin");
        html.ShouldContain("<h2 id=\"domains\">Mail domains</h2>");
    }
}

using System.Net;

using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;

using Nightware.Admin.Auth;
using Nightware.Admin.Pages.Account;
using Nightware.Admin.Tests.Support;
using Nightware.Application.Administration;
using Nightware.Application.Administration.Admins;
using Nightware.Domain.Kernel;

namespace Nightware.Admin.Tests.Pages;

public sealed class PasskeyPageTests : IDisposable
{
    private readonly ConsoleHost _host = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    private async Task<string> GetAsync(string query) =>
        await (await _host.ClientAs(ConsoleHost.GlobalAdmin).GetAsync(new Uri("/account/passkey" + query, UriKind.Relative), Ct)).Content.ReadAsStringAsync(Ct);

    [Fact]
    public async Task Every_page_links_the_signed_in_admin_to_add_a_passkey()
    {
        string html = await (await _host.ClientAs(ConsoleHost.EntityAdmin).GetAsync(new Uri("/", UriKind.Relative), Ct)).Content.ReadAsStringAsync(Ct);

        html.ShouldContain("href=\"/account/passkey\"");
    }

    [Fact]
    public async Task A_new_passkey_becomes_the_admins_default()
    {
        _host.Identities.PreferNewestPasskeyAsync(default!, Ct).ReturnsForAnyArgs(Result.Success());

        string html = await GetAsync("?status=success");

        html.ShouldContain("From your next sign-in, Keycloak asks for it");
        await _host.Identities.Received(1).PreferNewestPasskeyAsync(ConsoleHost.GlobalAdmin, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_passkey_that_could_not_be_made_the_default_says_how_to_reach_it()
    {
        _host.Identities.PreferNewestPasskeyAsync(default!, Ct).ReturnsForAnyArgs(Error.NotFound("The user has no passkey."));

        string html = await GetAsync("?status=success");

        html.ShouldContain("may still ask for your authenticator code first");
    }

    [Theory]
    [InlineData("?status=cancelled", "no passkey was added")]
    [InlineData("?status=error", "Keycloak did not add a passkey")]
    [InlineData("?status=%3Cb%3Epwned", "Add a passkey</button>")]
    public async Task Other_answers_change_nothing(string query, string expected)
    {
        string html = await GetAsync(query);

        html.ShouldContain(expected);
        html.ShouldNotContain("pwned");
        await _host.Identities.DidNotReceiveWithAnyArgs().PreferNewestPasskeyAsync(default!, Ct);
    }

    [Fact]
    public async Task Starting_needs_the_antiforgery_token()
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>());
        using HttpResponseMessage response = await _host.ClientAs(ConsoleHost.GlobalAdmin).PostAsync(new Uri("/account/passkey", UriKind.Relative), content, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public void Starting_asks_keycloak_for_webauthn_register_and_returns_to_the_page()
    {
        IUrlHelper url = Substitute.For<IUrlHelper>();
        url.ActionContext.Returns(new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()));
        url.RouteUrl(Arg.Any<UrlRouteContext>()).Returns("/console/account/passkey");
        var model = new PasskeyModel(new PreferOwnPasskey(Substitute.For<IAdminActor>(), Substitute.For<IIdentityAccounts>())) { Url = url };

        ChallengeResult challenge = model.OnPost().ShouldBeOfType<ChallengeResult>();

        challenge.AuthenticationSchemes.ShouldBe([OpenIdConnectDefaults.AuthenticationScheme]);
        challenge.Properties!.RedirectUri.ShouldBe("/console/account/passkey");
        challenge.Properties.Items[AdminAuthentication.RequiredActionItem].ShouldBe("webauthn-register");
    }
}

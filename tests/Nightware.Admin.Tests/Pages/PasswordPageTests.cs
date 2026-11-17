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

namespace Nightware.Admin.Tests.Pages;

public sealed class PasswordPageTests : IDisposable
{
    private readonly ConsoleHost _host = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Every_page_links_the_signed_in_admin_to_change_their_password()
    {
        string html = await (await _host.ClientAs(ConsoleHost.EntityAdmin).GetAsync(new Uri("/", UriKind.Relative), Ct)).Content.ReadAsStringAsync(Ct);

        html.ShouldContain("href=\"/account/password\"");
    }

    [Theory]
    [InlineData("success", "Your password has been changed")]
    [InlineData("cancelled", "your password is unchanged")]
    [InlineData("error", "Keycloak did not change your password")]
    public async Task The_page_reports_keycloaks_answer(string status, string expected)
    {
        string html = await (await _host.ClientAs(ConsoleHost.GlobalAdmin).GetAsync(new Uri($"/account/password?status={status}", UriKind.Relative), Ct)).Content.ReadAsStringAsync(Ct);

        html.ShouldContain(expected);
    }

    [Fact]
    public async Task An_unknown_status_is_not_echoed()
    {
        string html = await (await _host.ClientAs(ConsoleHost.GlobalAdmin).GetAsync(new Uri("/account/password?status=%3Cb%3Epwned", UriKind.Relative), Ct)).Content.ReadAsStringAsync(Ct);

        html.ShouldNotContain("pwned");
        html.ShouldContain("Change my password");
    }

    [Fact]
    public async Task Starting_a_change_needs_the_antiforgery_token()
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>());
        using HttpResponseMessage response = await _host.ClientAs(ConsoleHost.GlobalAdmin).PostAsync(new Uri("/account/password", UriKind.Relative), content, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public void Starting_a_change_asks_keycloak_for_update_password_and_returns_to_the_page()
    {
        IUrlHelper url = Substitute.For<IUrlHelper>();
        url.ActionContext.Returns(new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()));
        url.RouteUrl(Arg.Any<UrlRouteContext>()).Returns("/console/account/password");
        var model = new PasswordModel { Url = url };

        ChallengeResult challenge = model.OnPost().ShouldBeOfType<ChallengeResult>();

        challenge.AuthenticationSchemes.ShouldBe([OpenIdConnectDefaults.AuthenticationScheme]);
        challenge.Properties!.RedirectUri.ShouldBe("/console/account/password");
        challenge.Properties.Items[AdminAuthentication.RequiredActionItem].ShouldBe(AdminAuthentication.UpdatePasswordAction);
    }

    [Fact]
    public async Task Your_own_user_page_points_to_change_password_not_the_reset_tools()
    {
        _host.AddUser(ConsoleHost.EntityAdmin, "eve", "eve@acme.example");

        string html = await (await _host.ClientAs(ConsoleHost.EntityAdmin).GetAsync(new Uri($"/users/{ConsoleHost.EntityAdmin}", UriKind.Relative), Ct)).Content.ReadAsStringAsync(Ct);

        html.ShouldContain("This is you");
        html.ShouldNotContain("Generate a temporary password");
    }

    [Fact]
    public async Task Another_users_page_offers_the_reset_tools()
    {
        _host.AddUser("u-dan", "dan", "dan@acme.example");

        string html = await (await _host.ClientAs(ConsoleHost.EntityAdmin).GetAsync(new Uri("/users/u-dan", UriKind.Relative), Ct)).Content.ReadAsStringAsync(Ct);

        html.ShouldContain("Generate a temporary password");
        html.ShouldContain("E-mail a set-password link");
        html.ShouldNotContain("This is you");
    }
}

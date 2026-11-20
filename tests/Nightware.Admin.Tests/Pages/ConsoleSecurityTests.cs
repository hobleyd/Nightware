using System.Net;

using Nightware.Admin.Tests.Support;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Admin.Tests.Pages;

public sealed class ConsoleSecurityTests : IDisposable
{
    private readonly ConsoleHost _host = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Anonymous_requests_are_challenged()
    {
        using HttpResponseMessage response = await _host.CreateClient().GetAsync(new Uri("/", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Liveness_is_anonymous()
    {
        using HttpResponseMessage response = await _host.CreateClient().GetAsync(new Uri("/healthz/live", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_signed_in_non_admin_is_forbidden()
    {
        using HttpResponseMessage response = await _host.ClientAs(ConsoleHost.Nobody).GetAsync(new Uri("/", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Pages_carry_a_script_free_csp_and_no_store()
    {
        using HttpResponseMessage response = await _host.ClientAs(ConsoleHost.GlobalAdmin).GetAsync(new Uri("/", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string csp = response.Headers.GetValues("Content-Security-Policy").Single();
        csp.ShouldStartWith("default-src 'none'; style-src 'self'");
        csp.ShouldContain("form-action 'self' https://kc.example;");
        csp.ShouldContain("frame-ancestors 'none'");
        csp.ShouldNotContain("script-src");
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        response.Headers.GetValues("X-Content-Type-Options").Single().ShouldBe("nosniff");
    }

    [Fact]
    public async Task The_dashboard_shows_only_the_entity_admins_entities()
    {
        string html = await (await _host.ClientAs(ConsoleHost.EntityAdmin).GetAsync(new Uri("/", UriKind.Relative), Ct)).Content.ReadAsStringAsync(Ct);

        html.ShouldContain("Acme Pty");
        html.ShouldNotContain("Other Co");
        html.ShouldNotContain("Global admins");
    }

    [Fact]
    public async Task An_entity_admin_cannot_open_another_entity()
    {
        using HttpResponseMessage response = await _host.ClientAs(ConsoleHost.EntityAdmin).GetAsync(new Uri($"/entities/{ConsoleHost.Other}", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_entity_admin_cannot_open_the_global_admin_page()
    {
        using HttpResponseMessage response = await _host.ClientAs(ConsoleHost.EntityAdmin).GetAsync(new Uri("/admins", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_post_without_an_antiforgery_token_is_refused()
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["Form.Name"] = "Beta", ["Form.Alias"] = "beta", ["Form.Domain"] = "beta.example" });
        using HttpResponseMessage response = await _host.ClientAs(ConsoleHost.GlobalAdmin).PostAsync(new Uri("/entities", UriKind.Relative), content, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await _host.Entities.DidNotReceiveWithAnyArgs().CreateAsync(default!, Ct);
    }

    [Fact]
    public async Task A_forged_create_entity_post_by_an_entity_admin_is_refused_by_the_use_case()
    {
        HttpClient client = _host.ClientAs(ConsoleHost.EntityAdmin);

        using HttpResponseMessage response = await ConsoleHost.PostFormAsync(client, "/entities", "/entities",
            new Dictionary<string, string> { ["Form.Name"] = "Beta", ["Form.Alias"] = "beta", ["Form.Domain"] = "beta.example" });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await _host.Entities.DidNotReceiveWithAnyArgs().CreateAsync(default!, Ct);
    }

    [Fact]
    public async Task A_global_admin_adds_an_entity_from_the_tables_last_row()
    {
        string html = await (await _host.ClientAs(ConsoleHost.GlobalAdmin).GetAsync(new Uri("/entities", UriKind.Relative), Ct)).Content.ReadAsStringAsync(Ct);

        string footer = html[html.IndexOf("<tfoot>", StringComparison.Ordinal)..html.IndexOf("</tfoot>", StringComparison.Ordinal)];
        footer.ShouldContain("name=\"Form.Name\"");
        footer.ShouldContain("name=\"Form.Alias\"");
        footer.ShouldContain("name=\"Form.Domain\"");
        footer.ShouldContain("<button type=\"submit\" form=\"new-entity\" class=\"icon\" title=\"Create entity\" aria-label=\"Create entity\">");
        footer.Split("form=\"new-entity\"").Length.ShouldBe(5, "every field and the button belong to the form outside the table");
        html.ShouldContain("<form id=\"new-entity\" method=\"post\">");
        html.ShouldNotContain("<h2>New entity</h2>");
    }

    [Fact]
    public async Task An_entity_admin_has_no_row_to_add_an_entity()
    {
        string html = await (await _host.ClientAs(ConsoleHost.EntityAdmin).GetAsync(new Uri("/entities", UriKind.Relative), Ct)).Content.ReadAsStringAsync(Ct);

        html.ShouldNotContain("<tfoot>");
        html.ShouldNotContain("new-entity");
    }

    [Fact]
    public async Task A_global_admin_creates_an_entity()
    {
        _host.Entities.CreateAsync(Arg.Any<NewEntity>(), Arg.Any<CancellationToken>()).Returns(new Entity("e-beta", "beta", "Beta", [ConsoleHost.Domain("beta.example")]));

        using HttpResponseMessage response = await ConsoleHost.PostFormAsync(_host.ClientAs(ConsoleHost.GlobalAdmin), "/entities", "/entities",
            new Dictionary<string, string> { ["Form.Name"] = "Beta", ["Form.Alias"] = "beta", ["Form.Domain"] = "beta.example" });

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.ShouldBe("/entities/e-beta");
    }

    [Fact]
    public async Task Keycloak_being_unreachable_is_a_503_not_a_default_scope()
    {
        _host.Scopes.ResolveAsync(ConsoleHost.GlobalAdmin, Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("down"));

        using HttpResponseMessage response = await _host.ClientAs(ConsoleHost.GlobalAdmin).GetAsync(new Uri("/", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task The_scope_comes_from_keycloak_not_the_token()
    {
        // The same signed-in person loses global rights in Keycloak: the console follows Keycloak.
        _host.Scopes.ResolveAsync("u-demoted", Arg.Any<CancellationToken>()).Returns(new AdminScope(false, new HashSet<string>()));

        using HttpResponseMessage response = await _host.ClientAs("u-demoted").GetAsync(new Uri("/admins", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_offboarded_admin_loses_the_console_whatever_their_cookie_says()
    {
        _host.Scopes.ResolveAsync("u-offboarded", Arg.Any<CancellationToken>()).Returns(new AdminScope(true, new HashSet<string>(), AccountEnabled: false));

        using HttpResponseMessage response = await _host.ClientAs("u-offboarded").GetAsync(new Uri("/", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}

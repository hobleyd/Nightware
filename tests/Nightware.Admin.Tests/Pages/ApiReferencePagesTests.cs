using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

using Nightware.Admin.Tests.Support;

namespace Nightware.Admin.Tests.Pages;

public sealed class ApiReferencePagesTests : IAsyncDisposable
{
    private readonly ConsoleHost _host = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    [Fact]
    public async Task The_reference_runs_swagger_ui_from_this_origin_with_its_own_policy()
    {
        HttpClient client = _host.ClientAs(ConsoleHost.EntityAdmin);

        using HttpResponseMessage page = await client.GetAsync("/api", Ct);

        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        string html = await page.Content.ReadAsStringAsync(Ct);
        html.ShouldContain("lib/swagger-ui/swagger-ui-bundle.js");
        html.ShouldContain("js/api-reference.js");
        html.ShouldContain("data-url=\"/api/openapi.json\"");
        html.ShouldNotContain("<script>", Case.Insensitive, "the console runs no inline script");
        string csp = page.Headers.GetValues("Content-Security-Policy").Single();
        csp.ShouldContain("script-src 'self'");
        csp.ShouldNotContain("unsafe-eval");

        using HttpResponseMessage other = await client.GetAsync("/", Ct);
        string console = other.Headers.GetValues("Content-Security-Policy").Single();
        console.ShouldNotContain("script-src", Case.Sensitive, "other pages keep the console's policy");
        FormAction(csp).ShouldBe(FormAction(console), "sign-out still posts to Keycloak from this page");
    }

    private static string FormAction(string policy) =>
        policy.Split(';', StringSplitOptions.TrimEntries).Single(d => d.StartsWith("form-action ", StringComparison.Ordinal));

    [Fact]
    public void Widening_keeps_every_other_directive_of_the_consoles_policy()
    {
        var widened = Nightware.Admin.Pages.Api.IndexModel.Widen(
            "default-src 'none'; style-src 'self'; img-src 'self'; form-action 'self' https://id.example; frame-ancestors 'none'; base-uri 'none'");

        widened.ShouldBe("default-src 'none'; script-src 'self'; connect-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; form-action 'self' https://id.example; frame-ancestors 'none'; base-uri 'none'");
    }

    [Fact]
    public async Task The_document_and_swagger_ui_are_served()
    {
        HttpClient client = _host.ClientAs(ConsoleHost.EntityAdmin);

        using HttpResponseMessage document = await client.GetAsync("/api/openapi.json", Ct);
        document.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        JsonNode openApi = JsonNode.Parse(await document.Content.ReadAsStringAsync(Ct))!;
        openApi["openapi"]!.GetValue<string>().ShouldBe("3.1.0");
        openApi["paths"]!["/me/mailFolders"]!["get"].ShouldNotBeNull();

        (await client.GetAsync("/lib/swagger-ui/swagger-ui-bundle.js", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await client.GetAsync("/js/api-reference.js", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_reference_needs_a_signed_in_administrator()
    {
        using HttpClient anonymous = _host.CreateClient(new() { AllowAutoRedirect = false });

        (await anonymous.GetAsync("/api", Ct)).StatusCode.ShouldNotBe(HttpStatusCode.OK);
        (await anonymous.GetAsync("/api/openapi.json", Ct)).StatusCode.ShouldNotBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// The embedded document is rendered from the contract by tools/contract/render_openapi.py: every operation in
    /// docs/contract/endpoints.json is in it, and nothing else. Out of step, rerun the script.
    /// </summary>
    [Fact]
    public void The_document_lists_exactly_the_contracts_operations()
    {
        string root = RepositoryRoot();
        JsonArray contract = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "docs", "contract", "endpoints.json")))!.AsArray();
        JsonObject paths = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "src", "Nightware.Admin", "Api", "openapi.json")))!["paths"]!.AsObject();

        var expected = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonNode? op in contract)
        {
            string path = op!["path"]!.GetValue<string>();
            string[] forms = op["forms"]?.AsArray().Select(f => f!.GetValue<string>()).ToArray() ?? [];
            string method = op["method"]!.GetValue<string>().ToLowerInvariant();
            if (forms is ["external"])
            {
                continue;
            }

            if (path.StartsWith("{mailbox}", StringComparison.Ordinal))
            {
                string rest = path["{mailbox}".Length..];
                rest = rest.Length == 0 || rest.StartsWith('/') ? rest : "/" + rest;
                if (forms.Contains("me"))
                {
                    expected.Add($"{method} /me{rest}");
                }

                if (forms.Contains("users"))
                {
                    expected.Add($"{method} /users/{{userId}}{rest}");
                }
            }
            else
            {
                expected.Add($"{method} {(path.StartsWith('/') ? path : "/" + path)}");
            }
        }

        var documented = paths.SelectMany(p => p.Value!.AsObject().Select(m => $"{m.Key} {p.Key}")).ToHashSet(StringComparer.Ordinal);
        documented.ShouldBe(expected, ignoreOrder: true, "run python3 tools/contract/render_openapi.py");
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "global.json")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("No repository root above " + AppContext.BaseDirectory);
    }
}

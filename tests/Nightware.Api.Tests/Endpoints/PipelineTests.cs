using System.Net;
using System.Net.Http.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

using Nightware.Api.Features.Mail.Dto;
using Nightware.Api.Graph;
using Nightware.Api.Graph.OData;
using Nightware.Api.Graph.Routing;
using Nightware.Api.Tests.Support;
using Nightware.Application.Common.Query;
using Nightware.Application.Mail;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Api.Tests.Endpoints;

/// <summary>The whole request pipeline: auth, correlation, binding, error envelopes, fallbacks.</summary>
public sealed class PipelineTests : IAsyncLifetime
{
    private readonly IMailFolderReader _folders = Substitute.For<IMailFolderReader>();
    private TestHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await TestHost.StartAsync(
            services => services.AddSingleton(_folders),
            app =>
            {
                var graph = app.MapGroup(GraphPaths.V1).RequireAuthorization().AddEndpointFilter<GraphBindingFilter>();
                graph.MapMailboxGet("/probe", static (Mailbox mailbox, ODataQuery odata, PreferHeader prefer, HttpContext http) =>
                {
                    prefer.MarkTimeZoneApplied(http.Response);
                    return Results.Json(new { mailbox = mailbox.Ref.Address, isMe = mailbox.IsMe, top = odata.Query.Top });
                });
                graph.MapMailboxGet("/mailFolders", static async (Mailbox mailbox, ODataQuery odata, IMailFolderReader reader, CancellationToken ct) =>
                    GraphResults.Match(
                        await reader.ListAsync(mailbox.Ref, null, odata.Query, ct),
                        page => GraphResults.Page(page, MessageDtoMapper.ToDto, $"{mailbox.ContextSegment}/mailFolders", odata.Query)));
                graph.MapMailboxGet("/mailFolders/{folderId}", static async (Mailbox mailbox, string folderId, ODataQuery odata, IMailFolderReader reader, CancellationToken ct) =>
                    GraphResults.Match(
                        await reader.GetAsync(mailbox.Ref, new FolderRef.ById(new FolderId(folderId)), ct),
                        folder => GraphResults.Entity(MessageDtoMapper.ToDto(folder), $"{mailbox.ContextSegment}/mailFolders", odata.Query.Select)));
                graph.MapGet("/boom", static IResult () => throw new InvalidOperationException("secret internals"));
                graph.MapPost("/body", static (MailFolderDto folder) => Results.Ok(folder.DisplayName));
                graph.MapGet("/scheme", static (HttpContext http) => $"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}");
            });
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Missing_token_is_a_graph_401()
    {
        var response = await _host.Client.GetAsync("/v1.0/me/probe", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldStartWith("Bearer");
        response.Headers.Contains("request-id").ShouldBeTrue();
        var body = await response.ReadObjectAsync();
        body.Str("error.code").ShouldBe("InvalidAuthenticationToken");
        body.Str("error.message").ShouldBe("Access token is empty.");
        body["error"]!["innerError"]!["request-id"]!.GetValue<string>().ShouldBe(response.Headers.GetValues("request-id").Single());
    }

    [Theory]
    [InlineData("wrong-key")]
    [InlineData("expired")]
    [InlineData("wrong-audience")]
    [InlineData("garbage")]
    public async Task Invalid_tokens_are_a_graph_401(string kind)
    {
        var token = kind switch
        {
            "wrong-key" => TestHost.Token(key: new SymmetricSecurityKey(new byte[48])),
            "expired" => TestHost.Token(expires: DateTime.UtcNow.AddHours(-1)),
            "wrong-audience" => TestHost.Token(audience: "someone-else"),
            _ => "not.a.jwt",
        };

        var response = await _host.AuthorizedClient(token).GetAsync("/v1.0/me/probe", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldContain("invalid_token");
        (await response.ReadObjectAsync()).Str("error.code").ShouldBe("InvalidAuthenticationToken");
    }

    [Theory]
    [InlineData("/v1.0/me/probe", "me@example.com", true)]
    [InlineData("/v1.0/users/Shared@Example.com/probe", "shared@example.com", false)]
    [InlineData("/v1.0/users/shared%40example.com/probe", "shared@example.com", false)]
    public async Task Mailbox_routes_resolve_both_forms(string path, string mailbox, bool isMe)
    {
        var response = await _host.AuthorizedClient().GetAsync(path, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadObjectAsync();
        body.Str("mailbox").ShouldBe(mailbox);
        body["isMe"]!.GetValue<bool>().ShouldBe(isMe);
    }

    [Fact]
    public async Task A_directory_id_is_not_found_while_the_directory_port_is_a_stub()
    {
        var response = await _host.AuthorizedClient().GetAsync("/v1.0/users/1234-5678/probe", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.ReadObjectAsync()).Str("error.code").ShouldBe("ErrorItemNotFound");
    }

    [Fact]
    public async Task Me_without_an_email_claim_is_forbidden_not_unauthorized()
    {
        var response = await _host.AuthorizedClient(TestHost.Token(email: null)).GetAsync("/v1.0/me/probe", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Unsupported_odata_is_a_400_naming_the_construct()
    {
        var response = await _host.AuthorizedClient().GetAsync("/v1.0/me/probe?$count=true", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.ReadObjectAsync();
        body.Str("error.code").ShouldBe("invalidRequest");
        body.Str("error.message")!.ShouldContain("$count");
    }

    [Fact]
    public async Task Prefer_is_bound_and_reported_applied()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1.0/me/probe?%24top=5");
        request.Headers.Add("Prefer", "outlook.timezone=\"UTC\"");

        var response = await _host.AuthorizedClient().SendAsync(request, Ct);

        response.Headers.GetValues("Preference-Applied").Single().ShouldBe("outlook.timezone=\"UTC\"");
        (await response.ReadObjectAsync())["top"]!.GetValue<int>().ShouldBe(5);
    }

    [Theory]
    [InlineData("/v1.0/me/nothingHere", "nothingHere")]
    [InlineData("/beta/me/findRooms", "findRooms")]
    [InlineData("/v1.0/me/messages/abc/microsoft.graph.eventMessage/event", "event")]
    public async Task Unknown_graph_routes_are_a_graph_400(string path, string segment)
    {
        var response = await _host.AuthorizedClient().PostAsync(path, null, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.ReadObjectAsync();
        body.Str("error.code").ShouldBe("BadRequest");
        body.Str("error.message").ShouldBe($"Resource not found for the segment '{segment}'.");
    }

    [Fact]
    public async Task Unknown_graph_routes_still_require_a_token()
    {
        (await _host.Client.GetAsync("/v1.0/me/nothingHere", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Paths_outside_graph_are_plain_404s()
    {
        var response = await _host.Client.GetAsync("/elsewhere", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_stub_port_is_a_501()
    {
        var host = await TestHost.StartAsync(map: app =>
            app.MapGroup(GraphPaths.V1).RequireAuthorization().AddEndpointFilter<GraphBindingFilter>()
                .MapMailboxGet("/mailFolders", static async (Mailbox mailbox, IMailFolderReader reader, CancellationToken ct) =>
                    GraphResults.Match(await reader.ListAsync(mailbox.Ref, null, ItemQuery.Default, ct), _ => Results.Ok())));
        await using var _ = host;

        var response = await host.AuthorizedClient().GetAsync("/v1.0/me/mailFolders", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
        var body = await response.ReadObjectAsync();
        body.Str("error.code").ShouldBe("notImplemented");
        body.Str("error.message").ShouldBe("IMailFolderReader.ListAsync is not implemented yet.");
    }

    [Fact]
    public async Task Throttling_carries_retry_after_seconds()
    {
        _folders.ListAsync(default, default, default!, Arg.Any<CancellationToken>()).ReturnsForAnyArgs(new Error(ErrorKind.Throttled, "x", "slow") { RetryAfterSeconds = 7 });

        var response = await _host.AuthorizedClient().GetAsync("/v1.0/me/mailFolders", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter!.Delta.ShouldBe(TimeSpan.FromSeconds(7));
    }

    [Fact]
    public async Task Unhandled_exceptions_are_a_500_without_internals()
    {
        var response = await _host.AuthorizedClient().GetAsync("/v1.0/boom", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        var text = await response.Content.ReadAsStringAsync(Ct);
        text.ShouldNotContain("secret");
        text.ShouldNotContain("InvalidOperationException");
        (await response.ReadObjectAsync()).Str("error.code").ShouldBe("generalException");
    }

    [Fact]
    public async Task Bodiless_framework_errors_get_the_envelope()
    {
        using var content = new StringContent("{not json", System.Text.Encoding.UTF8, "application/json");

        var response = await _host.AuthorizedClient().PostAsync("/v1.0/body", content, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadObjectAsync()).Str("error.code").ShouldBe("BadRequest");
    }

    [Fact]
    public async Task Request_bodies_bind_case_insensitively()
    {
        var response = await _host.AuthorizedClient().PostAsJsonAsync("/v1.0/body", new { DisplayName = "Inbox" }, Ct);

        (await response.Content.ReadFromJsonAsync<string>(Ct)).ShouldBe("Inbox");
    }

    [Fact]
    public async Task Client_request_id_is_echoed_and_request_id_is_emitted()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1.0/me/probe");
        request.Headers.Add("client-request-id", "c-42");

        var response = await _host.AuthorizedClient().SendAsync(request, Ct);

        response.Headers.GetValues("client-request-id").Single().ShouldBe("c-42");
        Guid.TryParse(response.Headers.GetValues("request-id").Single(), out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Collections_project_and_link_the_next_page_on_the_configured_base()
    {
        _folders.ListAsync(new MailboxRef("me@example.com"), null, Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>())
            .Returns(new Page<MailFolder>([new MailFolder(new FolderId("F1"), "Inbox", new FolderId("R"), 1, 3, 120)], "adapter-page-2"));

        var response = await _host.AuthorizedClient().GetAsync("/v1.0/me/mailFolders?%24select=id%2CdisplayName&%24top=100&%24skip=0", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        var body = await response.ReadObjectAsync();
        body["@odata.context"]!.GetValue<string>().ShouldBe("https://graph.test.example/v1.0/$metadata#users('me%40example.com')/mailFolders(id,displayName)");
        body["@odata.nextLink"]!.GetValue<string>().ShouldBe("https://graph.test.example/v1.0/me/mailFolders?$select=id%2CdisplayName&$top=100&$skiptoken=adapter-page-2");
        body["value"]![0]!.AsObject().Select(p => p.Key).ShouldBe(["id", "displayName"]);
    }

    [Fact]
    public async Task Following_a_next_link_passes_the_skiptoken_and_original_options()
    {
        _folders.ListAsync(default, default, default!, Arg.Any<CancellationToken>()).ReturnsForAnyArgs(Page<MailFolder>.Empty);

        var response = await _host.AuthorizedClient().GetAsync("https://graph.test.example/v1.0/me/mailFolders?$select=id%2CdisplayName&$top=100&$skiptoken=adapter-page-2", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadObjectAsync()).ContainsKey("@odata.nextLink").ShouldBeFalse();
        await _folders.Received().ListAsync(Arg.Any<MailboxRef>(), null, Arg.Is<ItemQuery>(q => q.SkipToken == "adapter-page-2" && q.Top == 100 && q.Select!.Count == 2), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Entities_get_an_entity_context_and_drop_odata_type_under_select()
    {
        _folders.GetAsync(default, default!, Arg.Any<CancellationToken>()).ReturnsForAnyArgs(new MailFolder(new FolderId("F9"), "Deleted Items", null, 0, 0, 7));

        var response = await _host.AuthorizedClient().GetAsync("/v1.0/users/shared@example.com/mailFolders/deleteditems?$select=id", Ct);

        var body = await response.ReadObjectAsync();
        body["@odata.context"]!.GetValue<string>().ShouldBe("https://graph.test.example/v1.0/$metadata#users('shared%40example.com')/mailFolders(id)/$entity");
        body.Select(p => p.Key).ShouldBe(["@odata.context", "id"]);
    }

    [Fact]
    public async Task Forwarded_headers_rewrite_the_request_origin()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1.0/scheme");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-Host", "mail.example.org");
        request.Headers.Add("X-Forwarded-Prefix", "/graph");

        var response = await _host.AuthorizedClient().SendAsync(request, Ct);

        (await response.Content.ReadAsStringAsync(Ct)).ShouldBe("https://mail.example.org/graph");
    }
}

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.Options;

using Nightware.Api.Auth;
using Nightware.Api.Features.Files.Download;
using Nightware.Api.Features.Tasks.Upload;
using Nightware.Api.Graph.Json;
using Nightware.Domain.Files;

namespace Nightware.Api.Tests.Auth;

/// <summary>The one credential source, driven through both kinds of URL as routing leaves the request.</summary>
public sealed class SelfAuthorisingUrlCredentialSourceTests
{
    private const string ShareId = "u!aGk";
    private static readonly UploadUrlBinding Upload = new("alice@example.com", "l", "t", "s");

    private readonly SelfAuthorisingUrlSealer _sealer = new(Options.Create(new DeltaLinkOptions { SigningKey = "k" }), TimeProvider.System);

    private string SealedUpload() =>
        _sealer.Seal(UploadUrl.Kind, new SealedTicket("the-token", DateTimeOffset.UtcNow.AddMinutes(5)) { Name = "a", Size = 4 }, Upload.BoundTo);

    private string SealedDownload(ContentFormat format = ContentFormat.Pdf) =>
        _sealer.Seal(DownloadUrl.Kind, new SealedTicket("the-token", DateTimeOffset.UtcNow.AddMinutes(5)), new DownloadUrlBinding(ShareId, format).BoundTo);

    /// <summary>A request as the middleware sees it: routed, with the matched endpoint's metadata and route values.</summary>
    internal static HttpContext Routed(string method, SelfAuthorisingUrl? kind, string query, params (string Name, string Value)[] routeValues)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.QueryString = new QueryString(query);
        foreach (var (name, value) in routeValues)
        {
            context.Request.RouteValues[name] = value;
        }

        context.SetEndpoint(new RouteEndpoint(
            static _ => Task.CompletedTask, RoutePatternFactory.Parse("/x"), 0, kind is null ? EndpointMetadataCollection.Empty : new EndpointMetadataCollection(kind), null));
        return context;
    }

    private static (string, string)[] UploadRoute(string user = "alice@example.com", string list = "l", string task = "t", string session = "s") =>
        [("user", user), ("listId", list), ("taskId", task), ("sessionId", session)];

    private bool Resolve(HttpContext context, out SealedTicket? ticket) =>
        new SelfAuthorisingUrlCredentialSource(_sealer).TryResolve(context.Request, out ticket);

    [Fact]
    public void Recovers_the_ticket_for_a_put_to_its_own_route()
    {
        Resolve(Routed("PUT", UploadUrl.Kind, "?authtoken=" + SealedUpload(), UploadRoute()), out var ticket).ShouldBeTrue();

        ticket!.BearerToken.ShouldBe("the-token");
        ticket.Name.ShouldBe("a");
        ticket.Size.ShouldBe(4);
    }

    [Fact]
    public void The_mailbox_compares_case_insensitively() =>
        Resolve(Routed("PUT", UploadUrl.Kind, "?authtoken=" + SealedUpload(), UploadRoute(user: "Alice@Example.COM")), out _).ShouldBeTrue();

    [Theory]
    [InlineData("user")]
    [InlineData("list")]
    [InlineData("task")]
    [InlineData("session")]
    public void Another_matched_route_gets_nothing(string changed)
    {
        var route = changed switch
        {
            "user" => UploadRoute(user: "bob@example.com"),
            "list" => UploadRoute(list: "l2"),
            "task" => UploadRoute(task: "t2"),
            _ => UploadRoute(session: "s2"),
        };

        Resolve(Routed("PUT", UploadUrl.Kind, "?authtoken=" + SealedUpload(), route), out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    public void Other_methods_get_nothing(string method) =>
        Resolve(Routed(method, UploadUrl.Kind, "?authtoken=" + SealedUpload(), UploadRoute()), out _).ShouldBeFalse();

    [Fact]
    public void Two_tokens_are_refused()
    {
        var value = SealedUpload();

        Resolve(Routed("PUT", UploadUrl.Kind, $"?authtoken={value}&authtoken={value}", UploadRoute()), out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("?authtoken=")]
    [InlineData("?token=x")]
    public void Requests_without_a_token_get_nothing(string query) =>
        Resolve(Routed("PUT", UploadUrl.Kind, query, UploadRoute()), out _).ShouldBeFalse();

    [Fact]
    public void A_route_without_the_metadata_gets_nothing() =>
        Resolve(Routed("PUT", null, "?authtoken=" + SealedUpload(), UploadRoute()), out _).ShouldBeFalse();

    [Fact]
    public void An_upload_credential_never_opens_on_the_download_route() =>
        Resolve(Routed("GET", DownloadUrl.Kind, "?authtoken=" + SealedUpload(), ("shareId", ShareId)), out _).ShouldBeFalse();

    [Fact]
    public void Recovers_the_ticket_for_a_get_of_its_own_share_and_format()
    {
        Resolve(Routed("GET", DownloadUrl.Kind, "?format=pdf&authtoken=" + SealedDownload(), ("shareId", ShareId)), out var ticket).ShouldBeTrue();
        ticket!.BearerToken.ShouldBe("the-token");
        Resolve(Routed("GET", DownloadUrl.Kind, "?authtoken=" + SealedDownload(ContentFormat.Original), ("shareId", ShareId)), out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData("?authtoken=")]
    [InlineData("?format=pdf&format=pdf&authtoken=")]
    [InlineData("?format=html&authtoken=")]
    public void A_different_format_gets_nothing(string query) =>
        Resolve(Routed("GET", DownloadUrl.Kind, query + SealedDownload(), ("shareId", ShareId)), out _).ShouldBeFalse();

    [Fact]
    public void Another_share_gets_nothing() =>
        Resolve(Routed("GET", DownloadUrl.Kind, "?format=pdf&authtoken=" + SealedDownload(), ("shareId", "u!b3RoZXI")), out _).ShouldBeFalse();
}

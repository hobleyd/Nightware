using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

using Nightware.Api.Discovery;
using Nightware.Api.Graph.Json;

namespace Nightware.Api.Tests.Graph.Json;

public sealed class GraphLinkBuilderTests
{
    private static GraphLinkBuilder Builder(string v1 = "", string beta = "") =>
        new(Options.Create(new DiscoveryOptions { GraphBaseUrl = v1, GraphBetaBaseUrl = beta }));

    private static HttpRequest Request(string path, string query = "", string scheme = "http", string host = "internal:8080", string pathBase = "")
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString(host);
        context.Request.PathBase = pathBase;
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        return context.Request;
    }

    [Fact]
    public void Uses_the_configured_public_base_per_version()
    {
        var builder = Builder("https://mail.example.com/graph/v1.0/", "https://mail.example.com/graph/beta");

        builder.VersionRoot(Request("/v1.0/me/messages")).ShouldBe("https://mail.example.com/graph/v1.0");
        builder.VersionRoot(Request("/beta/me/findRooms")).ShouldBe("https://mail.example.com/graph/beta");
    }

    [Fact]
    public void Falls_back_to_the_request_origin()
    {
        Builder().VersionRoot(Request("/BETA/x", scheme: "https", host: "proxy.example.org", pathBase: "/graph"))
            .ShouldBe("https://proxy.example.org/graph/beta");
    }

    [Fact]
    public void Next_link_under_a_path_prefix_keeps_the_prefix()
    {
        // The dev stack: Caddy strips /graph, the façade sees /v1.0/... and builds on the configured base.
        var request = Request("/v1.0/me/messages", "?%24top=10", pathBase: "/graph");

        Builder("https://nightware.example.org/graph/v1.0").NextLink(request, "t")
            .ShouldBe("https://nightware.example.org/graph/v1.0/me/messages?$top=10&$skiptoken=t");
        Builder().NextLink(Request("/v1.0/me/messages", scheme: "https", host: "nightware.example.org", pathBase: "/graph"), "t")
            .ShouldBe("https://nightware.example.org/graph/v1.0/me/messages?$skiptoken=t");
    }

    [Fact]
    public void Relative_path_strips_the_version()
    {
        GraphLinkBuilder.RelativePath(Request("/v1.0/me/mailFolders('inbox')")).ShouldBe("/me/mailFolders('inbox')");
        GraphLinkBuilder.RelativePath(Request("/other")).ShouldBe("/other");
    }

    [Fact]
    public void Context_and_links()
    {
        var builder = Builder("https://g.example/v1.0");

        builder.Context(Request("/v1.0/me/messages"), "users('a')/messages").ShouldBe("https://g.example/v1.0/$metadata#users('a')/messages");
        builder.Link(Request("/v1.0/users/a@b.c/messages/delta"), [new("$deltatoken", "abc.def"), new("x y", "1&2")])
            .ShouldBe("https://g.example/v1.0/users/a@b.c/messages/delta?$deltatoken=abc.def&x%20y=1%262");
    }

    [Fact]
    public void Next_link_keeps_options_and_replaces_paging()
    {
        var request = Request("/v1.0/me/messages", "?%24filter=conversationId%20in%20(%27a%27)&%24top=200&%24skip=25&%24skiptoken=old");

        Builder("https://g.example/v1.0").NextLink(request, "new")
            .ShouldBe("https://g.example/v1.0/me/messages?$filter=conversationId%20in%20%28%27a%27%29&$top=200&$skiptoken=new");
    }
}

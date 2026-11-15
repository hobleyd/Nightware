using Microsoft.AspNetCore.Http;

using Nightware.Api.Auth;

namespace Nightware.Api.Tests.Auth;

public sealed class SelfAuthorisingUrlMiddlewareTests
{
    private static readonly SelfAuthorisingUrl Kind = new("test/kind", "GET", TimeSpan.FromMinutes(5), static _ => []);
    private static readonly SelfAuthorisingUrl UrlOnlyKind = new("test/url-only", "PUT", TimeSpan.FromMinutes(5), static _ => [], urlOnly: true);

    private static async Task<(string? Authorization, SealedTicket? Ticket)> SeenDownstream(HttpContext context, params IRequestCredentialSource[] sources)
    {
        (string?, SealedTicket?) seen = default;
        var middleware = new SelfAuthorisingUrlMiddleware(
            ctx =>
            {
                seen = (ctx.Request.Headers.Authorization, ctx.Features.Get<SealedTicket>());
                return Task.CompletedTask;
            },
            sources);
        await middleware.InvokeAsync(context);
        return seen;
    }

    private static HttpContext Routed(SelfAuthorisingUrl? kind, string? authorization = null)
    {
        var context = SelfAuthorisingUrlCredentialSourceTests.Routed("GET", kind, string.Empty);
        if (authorization is not null)
        {
            context.Request.Headers.Authorization = authorization;
        }

        return context;
    }

    private static IRequestCredentialSource Source(string? token)
    {
        var source = Substitute.For<IRequestCredentialSource>();
        source.TryResolve(Arg.Any<HttpRequest>(), out Arg.Any<SealedTicket?>())
            .Returns(call =>
            {
                call[1] = token is null ? null : new SealedTicket(token, DateTimeOffset.MaxValue);
                return token is not null;
            });
        return source;
    }

    [Fact]
    public async Task Recovered_token_becomes_the_bearer_header_and_the_ticket_a_feature()
    {
        var (authorization, ticket) = await SeenDownstream(Routed(Kind), Source("abc"));

        authorization.ShouldBe("Bearer abc");
        ticket!.BearerToken.ShouldBe("abc");
    }

    [Fact]
    public async Task Existing_authorization_header_is_never_replaced_on_an_ordinary_kind()
    {
        var source = Source("other");

        var (authorization, ticket) = await SeenDownstream(Routed(Kind, "Bearer original"), source);

        authorization.ShouldBe("Bearer original");
        ticket.ShouldBeNull();
        source.DidNotReceiveWithAnyArgs().TryResolve(default!, out Arg.Any<SealedTicket?>());
    }

    [Fact]
    public async Task A_url_only_route_ignores_the_authorization_header()
    {
        var (authorization, ticket) = await SeenDownstream(Routed(UrlOnlyKind, "Bearer original"), Source(null));

        authorization.ShouldBeNullOrEmpty();
        ticket.ShouldBeNull();
    }

    [Fact]
    public async Task A_url_only_route_takes_its_credential_from_the_url_even_beside_a_header()
    {
        var (authorization, _) = await SeenDownstream(Routed(UrlOnlyKind, "Bearer original"), Source("from-url"));

        authorization.ShouldBe("Bearer from-url");
    }

    [Fact]
    public async Task Routes_without_the_metadata_are_never_consulted()
    {
        var source = Source("abc");

        var (authorization, _) = await SeenDownstream(Routed(null), source);

        authorization.ShouldBeNullOrEmpty();
        source.DidNotReceiveWithAnyArgs().TryResolve(default!, out Arg.Any<SealedTicket?>());
    }

    [Fact]
    public async Task First_source_that_recognises_the_request_wins()
    {
        var (authorization, _) = await SeenDownstream(Routed(Kind), Source(null), Source("second"), Source("third"));

        authorization.ShouldBe("Bearer second");
    }

    [Fact]
    public async Task Unrecognised_request_passes_through_without_a_header()
    {
        var (authorization, _) = await SeenDownstream(Routed(Kind), Source(null));

        authorization.ShouldBeNullOrEmpty();
    }

    [Fact]
    public async Task No_sources_is_a_no_op()
    {
        var (authorization, _) = await SeenDownstream(Routed(Kind));

        authorization.ShouldBeNullOrEmpty();
    }
}

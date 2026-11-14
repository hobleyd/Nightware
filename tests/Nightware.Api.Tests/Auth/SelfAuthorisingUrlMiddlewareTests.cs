using Microsoft.AspNetCore.Http;
using Nightware.Api.Auth;

namespace Nightware.Api.Tests.Auth;

public sealed class SelfAuthorisingUrlMiddlewareTests
{
    private static async Task<string?> AuthorizationSeenDownstream(HttpContext context, params IRequestCredentialSource[] sources)
    {
        string? seen = null;
        var middleware = new SelfAuthorisingUrlMiddleware(
            ctx =>
            {
                seen = ctx.Request.Headers.Authorization;
                return Task.CompletedTask;
            },
            sources);
        await middleware.InvokeAsync(context);
        return seen;
    }

    private static IRequestCredentialSource Source(string? token)
    {
        var source = Substitute.For<IRequestCredentialSource>();
        source.TryResolve(Arg.Any<HttpRequest>(), out Arg.Any<string>())
            .Returns(call =>
            {
                call[1] = token ?? string.Empty;
                return token is not null;
            });
        return source;
    }

    [Fact]
    public async Task Recovered_token_becomes_the_bearer_header()
    {
        var seen = await AuthorizationSeenDownstream(new DefaultHttpContext(), Source("abc"));

        seen.ShouldBe("Bearer abc");
    }

    [Fact]
    public async Task Existing_authorization_header_is_never_replaced()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer original";
        var source = Source("other");

        var seen = await AuthorizationSeenDownstream(context, source);

        seen.ShouldBe("Bearer original");
        source.DidNotReceiveWithAnyArgs().TryResolve(default!, out _);
    }

    [Fact]
    public async Task First_source_that_recognises_the_request_wins()
    {
        var seen = await AuthorizationSeenDownstream(new DefaultHttpContext(), Source(null), Source("second"), Source("third"));

        seen.ShouldBe("Bearer second");
    }

    [Fact]
    public async Task Unrecognised_request_passes_through_without_a_header()
    {
        var seen = await AuthorizationSeenDownstream(new DefaultHttpContext(), Source(null));

        seen.ShouldBeNullOrEmpty();
    }

    [Fact]
    public async Task No_sources_is_a_no_op()
    {
        var seen = await AuthorizationSeenDownstream(new DefaultHttpContext());

        seen.ShouldBeNullOrEmpty();
    }
}

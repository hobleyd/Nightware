using System.Security.Claims;

using Microsoft.AspNetCore.Http;

using Nightware.Api.Auth;
using Nightware.Api.Hosting;

namespace Nightware.Api.Tests.Auth;

public sealed class HttpUserContextTests
{
    private static ClaimsPrincipal User(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(c => new Claim(c.Type, c.Value)), authenticationType: "Bearer"));

    [Fact]
    public void Reads_oidc_claims()
    {
        var principal = HttpUserContext.FromClaims(User(("sub", "u1"), ("preferred_username", "Alice"), ("email", "Alice@Example.com"), ("name", "Alice A")));

        principal.Id.ShouldBe("u1");
        principal.Upn.ShouldBe("alice");
        principal.Mail.ShouldBe("alice@example.com");
        principal.DisplayName.ShouldBe("Alice A");
    }

    [Fact]
    public void Uses_an_address_shaped_username_when_there_is_no_email()
    {
        var principal = HttpUserContext.FromClaims(User(("sub", "u1"), ("preferred_username", "alice@example.com")));

        principal.Mail.ShouldBe("alice@example.com");
        principal.DisplayName.ShouldBe("alice@example.com");
    }

    [Fact]
    public void Leaves_mail_empty_when_nothing_is_an_address()
    {
        var principal = HttpUserContext.FromClaims(User(("sub", "u1"), ("preferred_username", "alice")));

        principal.Mail.ShouldBeEmpty();
        principal.Upn.ShouldBe("alice");
    }

    [Fact]
    public void Falls_back_to_sub_for_display_name_and_mail_for_upn()
    {
        HttpUserContext.FromClaims(User(("sub", "u1"))).DisplayName.ShouldBe("u1");
        HttpUserContext.FromClaims(User(("sub", "u1"), ("email", "a@b.c"))).Upn.ShouldBe("a@b.c");
    }

    [Fact]
    public void Rejects_anonymous_or_subjectless_callers()
    {
        Should.Throw<InvalidOperationException>(() => HttpUserContext.FromClaims(new ClaimsPrincipal(new ClaimsIdentity())));
        Should.Throw<InvalidOperationException>(() => HttpUserContext.FromClaims(User(("email", "a@b.c"))));
    }

    [Fact]
    public void Exposes_principal_and_bearer_token_of_the_current_request()
    {
        var http = new DefaultHttpContext { User = User(("sub", "u1"), ("email", "a@b.c")) };
        http.Request.Headers.Authorization = "Bearer abc.def.ghi";
        var context = new HttpUserContext(new HttpContextAccessor { HttpContext = http });

        context.BearerToken.ShouldBe("abc.def.ghi");
        context.Principal.Id.ShouldBe("u1");
        context.Principal.ShouldBeSameAs(context.Principal);
    }

    [Fact]
    public void Throws_outside_a_request_or_without_a_bearer()
    {
        Should.Throw<InvalidOperationException>(() => new HttpUserContext(new HttpContextAccessor()).Principal);
        var http = new DefaultHttpContext();
        http.Request.Headers.Authorization = "Basic xyz";
        Should.Throw<InvalidOperationException>(() => new HttpUserContext(new HttpContextAccessor { HttpContext = http }).BearerToken);
    }

    [Fact]
    public void Prefers_the_scope_s_own_request_over_the_accessor()
    {
        var outer = new DefaultHttpContext { User = User(("sub", "outer"), ("email", "outer@b.c")) };
        var inner = new DefaultHttpContext { User = User(("sub", "inner"), ("email", "inner@b.c")) };
        inner.Request.Headers.Authorization = "Bearer inner-token";

        var context = new HttpUserContext(new HttpContextAccessor { HttpContext = outer }, new CurrentRequest { Context = inner });

        context.Principal.Id.ShouldBe("inner");
        context.BearerToken.ShouldBe("inner-token");
    }
}

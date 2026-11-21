using System.Security.Claims;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;

using Nightware.Api.Auth;

namespace Nightware.Api.Tests.Auth;

public sealed class JwtBearerConfigurationTests
{
    [Fact]
    public void Maps_auth_options_onto_jwt_bearer()
    {
        var auth = new AuthOptions
        {
            Authority = "https://auth.example/realms/nightware",
            MetadataAddress = "http://keycloak:8080/realms/nightware/.well-known/openid-configuration",
            Audience = "nightware-api",
            RequireHttpsMetadata = false,
        };
        auth.ValidIssuers.Add("http://keycloak:8080/realms/nightware");
        var options = new JwtBearerOptions();

        AuthServiceCollectionExtensions.ConfigureJwtBearer(options, auth);

        options.MapInboundClaims.ShouldBeFalse();
        options.Authority.ShouldBe(auth.Authority);
        options.MetadataAddress.ShouldBe(auth.MetadataAddress);
        options.RequireHttpsMetadata.ShouldBeFalse();
        options.Audience.ShouldBe("nightware-api");
        options.TokenValidationParameters.ValidIssuers.ShouldBe(["http://keycloak:8080/realms/nightware", auth.Authority]);
        options.TokenValidationParameters.NameClaimType.ShouldBe("preferred_username");
        options.Events.OnChallenge.ShouldNotBeNull();
    }

    [Fact]
    public void Empty_values_leave_defaults()
    {
        var options = new JwtBearerOptions();

        AuthServiceCollectionExtensions.ConfigureJwtBearer(options, new AuthOptions { Authority = " " });

        options.Authority.ShouldBeNull();
        options.MetadataAddress.ShouldBeNullOrEmpty();
        options.TokenValidationParameters.ValidateAudience.ShouldBeFalse();
        options.TokenValidationParameters.ValidIssuers.ShouldBeNull();
    }

    [Theory]
    [InlineData("true", true, true)]
    [InlineData("True", true, true)]
    [InlineData("false", true, false)]
    [InlineData(null, true, false)]
    [InlineData(null, false, true)]
    public async Task The_default_policy_requires_a_verified_email_unless_turned_off(string? claim, bool required, bool allowed)
    {
        var policy = AuthServiceCollectionExtensions.DefaultPolicy(new AuthOptions { RequireVerifiedEmail = required });
        List<Claim> claims = [new("sub", "u")];
        if (claim is not null)
        {
            claims.Add(new Claim("email_verified", claim));
        }

        var context = new AuthorizationHandlerContext(policy.Requirements, new ClaimsPrincipal(new ClaimsIdentity(claims, "test")), null);
        foreach (var handler in policy.Requirements.OfType<IAuthorizationHandler>())
        {
            await handler.HandleAsync(context);
        }

        context.HasSucceeded.ShouldBe(allowed);
        policy.Requirements.ShouldContain(r => r is DenyAnonymousAuthorizationRequirement);
    }
}

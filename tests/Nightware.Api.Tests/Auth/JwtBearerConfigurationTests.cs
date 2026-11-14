using Microsoft.AspNetCore.Authentication.JwtBearer;

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
}

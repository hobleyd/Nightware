using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Nightware.Api.Graph.Errors;
using Nightware.Api.Hosting;
using Nightware.Application.Common.Identity;

namespace Nightware.Api.Auth;

public static class AuthServiceCollectionExtensions
{
    /// <summary>JWT bearer authentication against the configured issuer, Graph-shaped 401/403, and <see cref="IUserContext"/>.</summary>
    public static IServiceCollection AddGraphAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var auth = new AuthOptions();
        configuration.GetSection(AuthOptions.SectionName).Bind(auth);

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options => ConfigureJwtBearer(options, auth));
        services.AddAuthorization();

        services.AddHttpContextAccessor();
        services.TryAddScoped<CurrentRequest>();
        // The request's caller, unless the scope is the change notifier's, acting as a subscriber.
        services.TryAddScoped<HttpUserContext>();
        services.TryAddScoped<IUserContext>(static sp =>
            sp.GetService<Nightware.Application.Subscriptions.SubscriberContext>()?.Current ?? sp.GetRequiredService<HttpUserContext>());
        return services;
    }

    /// <summary>Self-authorising URLs (ADR 0001 §39): the sealer that mints them and the source the middleware asks when a request has no bearer.</summary>
    public static IServiceCollection AddSelfAuthorisingUrls(this IServiceCollection services)
    {
        services.TryAddSingleton<SelfAuthorisingUrlSealer>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRequestCredentialSource, SelfAuthorisingUrlCredentialSource>());
        return services;
    }

    internal static void ConfigureJwtBearer(JwtBearerOptions options, AuthOptions auth)
    {
        options.MapInboundClaims = false;
        options.Authority = string.IsNullOrWhiteSpace(auth.Authority) ? null : auth.Authority;
        if (!string.IsNullOrWhiteSpace(auth.MetadataAddress))
        {
            options.MetadataAddress = auth.MetadataAddress;
        }

        options.RequireHttpsMetadata = auth.RequireHttpsMetadata;
        options.TokenValidationParameters.NameClaimType = HttpUserContext.UsernameClaim;
        if (string.IsNullOrWhiteSpace(auth.Audience))
        {
            options.TokenValidationParameters.ValidateAudience = false;
        }
        else
        {
            options.Audience = auth.Audience;
        }

        if (auth.ValidIssuers.Count > 0)
        {
            var issuers = auth.ValidIssuers.ToList();
            if (options.Authority is not null)
            {
                issuers.Add(options.Authority);
            }

            options.TokenValidationParameters.ValidIssuers = issuers;
        }

        options.Events = new JwtBearerEvents
        {
            OnChallenge = WriteGraphChallengeAsync,
            OnForbidden = static context => GraphErrorWriter.WriteAsync(context.HttpContext, new GraphErrorDescriptor(
                StatusCodes.Status403Forbidden,
                "ErrorAccessDenied",
                "Access is denied. Check credentials and try again.",
                null)),
        };
    }

    /// <summary>Graph answers a missing or bad token with 401 <c>InvalidAuthenticationToken</c>; keep the WWW-Authenticate challenge.</summary>
    internal static async Task WriteGraphChallengeAsync(JwtBearerChallengeContext context)
    {
        context.HandleResponse();
        var response = context.Response;
        if (response.HasStarted)
        {
            return;
        }

        var challenge = "Bearer";
        if (!string.IsNullOrEmpty(context.Error))
        {
            challenge += $" error=\"{context.Error}\"";
        }

        response.Headers.WWWAuthenticate = challenge;
        var message = context.AuthenticateFailure is null
            ? (string.IsNullOrEmpty(context.Request.Headers.Authorization) ? "Access token is empty." : "Access token validation failure.")
            : "Access token validation failure. Invalid audience, issuer, signature or lifetime.";
        await GraphErrorWriter.WriteAsync(context.HttpContext, new GraphErrorDescriptor(
            StatusCodes.Status401Unauthorized,
            "InvalidAuthenticationToken",
            message,
            null)).ConfigureAwait(false);
    }
}

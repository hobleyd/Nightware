using System.Globalization;
using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Admin.Auth;

/// <summary>
/// The request's <see cref="IAdminActor"/>. The identity (<c>sub</c>, <c>auth_time</c>, <c>acr</c>) comes from
/// the session cookie; the scope is re-resolved from Keycloak by <see cref="AdminActorMiddleware"/>, never
/// trusted from the token, so a revoked role stops working within <see cref="AdminAuthOptions.ScopeCacheLifetime"/>.
/// </summary>
public sealed class SessionAdminActor : IAdminActor
{
    private static readonly AdminActor Anonymous = new(string.Empty, false, new HashSet<string>(), DateTimeOffset.MinValue, null);

    public AdminActor Current { get; private set; } = Anonymous;

    internal void Set(AdminActor actor) => Current = actor;
}

/// <summary>Resolves <see cref="SessionAdminActor"/> after authentication, before authorization.</summary>
public sealed class AdminActorMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, SessionAdminActor actor, IAdminScopeSource scopes, IMemoryCache cache, IOptions<AdminAuthOptions> options)
    {
        ClaimsPrincipal user = context.User;
        string? sub = user.FindFirstValue("sub");
        if (user.Identity?.IsAuthenticated == true && !string.IsNullOrEmpty(sub))
        {
            Result<AdminScope> scope = await cache.GetOrCreateAsync(
                    "admin-scope:" + sub,
                    async entry =>
                    {
                        Result<AdminScope> resolved = await scopes.ResolveAsync(sub, context.RequestAborted).ConfigureAwait(false);
                        entry.AbsoluteExpirationRelativeToNow = resolved.IsSuccess ? options.Value.ScopeCacheLifetime : TimeSpan.FromSeconds(1);
                        return resolved;
                    })
                .ConfigureAwait(false);
            if (!scope.IsSuccess)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await context.Response.WriteAsync("The console cannot reach Keycloak to check your permissions. Try again shortly.", context.RequestAborted).ConfigureAwait(false);
                return;
            }

            actor.Set(new AdminActor(sub, scope.Value.IsGlobal, scope.Value.EntityIds, AuthenticatedAt(user), user.FindFirstValue(AdminAuthentication.AcrClaim)));
        }

        await next(context).ConfigureAwait(false);
    }

    internal static DateTimeOffset AuthenticatedAt(ClaimsPrincipal user) =>
        long.TryParse(user.FindFirstValue(AdminAuthentication.AuthTimeClaim), NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : DateTimeOffset.MinValue;
}

/// <summary>The console's one authorization policy: signed in and an administrator of something.</summary>
public sealed class AdminRequirement : IAuthorizationRequirement
{
    public const string PolicyName = "ConsoleAdmin";
}

internal sealed class AdminRequirementHandler(SessionAdminActor actor) : AuthorizationHandler<AdminRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AdminRequirement requirement)
    {
        if (actor.Current.IsAdmin)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

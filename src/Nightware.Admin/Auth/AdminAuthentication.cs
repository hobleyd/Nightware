using System.Globalization;
using System.Security.Claims;

using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Nightware.Admin.Auth;

/// <summary>
/// Sign-in for the console (ADR 0002 D7): OIDC authorization code + PKCE against the console's own client,
/// an encrypted cookie session, and no token ever kept — the ID token is validated, its claims copied,
/// and it is dropped. The console calls backends with its own service credentials, never the admin's.
/// </summary>
public static class AdminAuthentication
{
    public const string CookieName = "__Secure-nightware.console";

    /// <summary>The <c>acr</c> claim; kept as issued (inbound claim mapping is off).</summary>
    public const string AcrClaim = "acr";

    /// <summary>The <c>auth_time</c> claim: when the person last proved who they are.</summary>
    public const string AuthTimeClaim = "auth_time";

    /// <summary>
    /// When the session began, in the ticket's properties. Unlike <c>IssuedUtc</c>, which every sliding refresh
    /// renews, it survives refreshes, so the absolute lifetime really is absolute.
    /// </summary>
    public const string SessionStartedItem = "nightware.session_started";

    /// <summary>Allowance between Keycloak's clock and the console's for the re-authentication check.</summary>
    internal const int ClockSkewSeconds = 60;

    /// <summary>Set on a challenge that must re-prompt for credentials (<c>max_age=0</c>).</summary>
    public const string ForceReauthenticationItem = "nightware.reauth";

    /// <summary>
    /// Set on a challenge that asks Keycloak to run a required action for the signed-in person (an
    /// application-initiated action, <c>kc_action</c>), such as changing their own password.
    /// </summary>
    public const string RequiredActionItem = "nightware.kc_action";

    /// <summary>Keycloak's change-password action.</summary>
    public const string UpdatePasswordAction = "UPDATE_PASSWORD";

    /// <summary>Keycloak's add-a-passkey action (a passkey or security key as the second factor).</summary>
    public const string RegisterPasskeyAction = "webauthn-register";

    /// <summary>The query parameter that carries Keycloak's answer to a required action back to the console page.</summary>
    public const string ActionStatusParameter = "status";

    public static IServiceCollection AddAdminAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(static o =>
            {
                o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                o.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
            })
            .AddCookie()
            .AddOpenIdConnect();

        services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
            .Configure<IOptions<AdminAuthOptions>>(static (cookie, admin) => ConfigureCookie(cookie, admin.Value));
        services.AddOptions<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme)
            .Configure<IOptions<AdminAuthOptions>>(static (oidc, admin) => ConfigureOidc(oidc, admin.Value));
        return services;
    }

    internal static void ConfigureCookie(CookieAuthenticationOptions cookie, AdminAuthOptions admin)
    {
        cookie.Cookie.Name = CookieName;
        cookie.Cookie.HttpOnly = true;
        cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        // Lax, not Strict: the session cookie must ride along on the top-level redirect back from Keycloak.
        // Every state-changing request is a POST with an antiforgery token, which Lax never sends cross-site.
        cookie.Cookie.SameSite = SameSiteMode.Lax;
        cookie.ExpireTimeSpan = admin.IdleTimeout;
        cookie.SlidingExpiration = true;
        cookie.LoginPath = "/account/signin";
        cookie.AccessDeniedPath = "/account/denied";
        cookie.Events.OnValidatePrincipal = context =>
        {
            TimeProvider time = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>();
            if (!context.Properties.Items.TryGetValue(SessionStartedItem, out string? started)
                || !long.TryParse(started, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds)
                || time.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(seconds) > admin.AbsoluteLifetime)
            {
                context.RejectPrincipal();
                return context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            }

            return Task.CompletedTask;
        };
    }

    internal static void ConfigureOidc(OpenIdConnectOptions oidc, AdminAuthOptions admin)
    {
        oidc.Authority = admin.Authority;
        if (!string.IsNullOrEmpty(admin.MetadataAddress))
        {
            oidc.MetadataAddress = admin.MetadataAddress;
        }

        oidc.RequireHttpsMetadata = admin.RequireHttpsMetadata;
        oidc.ClientId = admin.ClientId;
        oidc.ClientSecret = admin.ClientSecret;
        oidc.ResponseType = OpenIdConnectResponseType.Code;
        oidc.ResponseMode = OpenIdConnectResponseMode.Query;
        oidc.UsePkce = true;
        oidc.SaveTokens = false;
        oidc.GetClaimsFromUserInfoEndpoint = false;
        oidc.MapInboundClaims = false;
        // The handler's default claim actions delete acr and auth_time from the identity; the console needs both
        // (MFA and freshness checks), so keep them.
        oidc.ClaimActions.Remove(AcrClaim);
        oidc.ClaimActions.Remove(AuthTimeClaim);
        oidc.Scope.Clear();
        oidc.Scope.Add("openid");
        oidc.Scope.Add("profile");
        oidc.Scope.Add("email");
        oidc.CallbackPath = "/signin-oidc";
        oidc.SignedOutCallbackPath = "/signout-callback-oidc";
        oidc.SignedOutRedirectUri = "/account/signed-out";
        oidc.TokenValidationParameters.NameClaimType = "preferred_username";
        oidc.TokenValidationParameters.ValidAudience = admin.ClientId;

        oidc.Events.OnRedirectToIdentityProvider = context =>
        {
            if (admin.AcceptedAcrValues.Count > 0)
            {
                context.ProtocolMessage.AcrValues = string.Join(' ', admin.AcceptedAcrValues);
            }

            if (context.Properties.Items.ContainsKey(ForceReauthenticationItem))
            {
                context.ProtocolMessage.MaxAge = "0";
                context.ProtocolMessage.Prompt = "login";
                // When the console asked, so the answer can be checked against it (the properties travel in the
                // protected state parameter).
                TimeProvider time = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>();
                context.Properties.Items[ForceReauthenticationItem] = time.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            }

            if (context.Properties.Items.TryGetValue(RequiredActionItem, out string? action) && action is UpdatePasswordAction or RegisterPasskeyAction)
            {
                context.ProtocolMessage.SetParameter("kc_action", action);
            }

            return Task.CompletedTask;
        };
        oidc.Events.OnTokenValidated = context =>
        {
            Claim? acr = context.Principal?.FindFirst(AcrClaim);
            if (!AcrValidator.IsAccepted(acr?.Value, admin.AcceptedAcrValues))
            {
                context.Fail("The sign-in did not use multi-factor authentication.");
                return Task.CompletedTask;
            }

            // auth_time is the evidence for every freshness check; a token without it is refused, not trusted.
            if (!long.TryParse(context.Principal?.FindFirst(AuthTimeClaim)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long authTime))
            {
                context.Fail("The sign-in carried no auth_time.");
                return Task.CompletedTask;
            }

            // A re-authentication the console forced must have happened after it was asked for, not merely
            // been answered by an older session.
            if (context.Properties?.Items.TryGetValue(ForceReauthenticationItem, out string? askedAt) == true
                && long.TryParse(askedAt, NumberStyles.Integer, CultureInfo.InvariantCulture, out long asked)
                && authTime < asked - ClockSkewSeconds)
            {
                context.Fail("Keycloak did not re-authenticate the user.");
                return Task.CompletedTask;
            }

            // Each credential sign-in (re-authentication included) starts the session clock; refreshes never do.
            TimeProvider time = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>();
            context.Properties ??= new AuthenticationProperties();
            context.Properties.Items.TryAdd(SessionStartedItem, time.GetUtcNow().ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));

            // Only the ID token is kept (in the encrypted cookie), as the id_token_hint Keycloak needs to end the
            // SSO session at sign-out. No access or refresh token is ever stored.
            // In the code flow the ID token comes from the token endpoint, not the authorization response.
            if ((context.TokenEndpointResponse?.IdToken ?? context.ProtocolMessage?.IdToken) is { Length: > 0 } idToken)
            {
                context.Properties.StoreTokens([new AuthenticationToken { Name = "id_token", Value = idToken }]);
            }

            // Keycloak answers a required action on the authorization response (kc_action_status); only the three
            // values it documents reach the page, and the marker is not kept in the session.
            if (context.Properties.Items.Remove(RequiredActionItem))
            {
                string status = context.ProtocolMessage?.GetParameter("kc_action_status") switch
                {
                    "success" => "success",
                    "cancelled" => "cancelled",
                    _ => "error",
                };
                context.Properties.RedirectUri = QueryHelpers.AddQueryString(context.Properties.RedirectUri ?? "/", ActionStatusParameter, status);
            }

            return Task.CompletedTask;
        };
        oidc.Events.OnRedirectToIdentityProviderForSignOut = context =>
        {
            // Keycloak requires id_token_hint or client_id with post_logout_redirect_uri.
            context.ProtocolMessage.ClientId = admin.ClientId;
            return Task.CompletedTask;
        };
        oidc.Events.OnRemoteFailure = static context =>
        {
            context.HandleResponse();
            context.Response.Redirect(context.Request.PathBase + "/account/denied?reason=signin");
            return Task.CompletedTask;
        };
    }
}

/// <summary>Whether an <c>acr</c> value is one the console accepts. Empty accepted list = any (non-production).</summary>
public static class AcrValidator
{
    public static bool IsAccepted(string? acr, IEnumerable<string> accepted)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        string[] values = [.. accepted];
        return values.Length == 0 || (acr is not null && values.Contains(acr, StringComparer.Ordinal));
    }
}

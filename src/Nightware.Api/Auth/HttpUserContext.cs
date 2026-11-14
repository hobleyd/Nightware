using System.Security.Claims;

using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Auth;

/// <summary><see cref="IUserContext"/> read from the authenticated request's claims and bearer token.</summary>
/// <remarks>
/// Claims are the raw OIDC names (<c>MapInboundClaims = false</c>): <c>sub</c>,
/// <c>preferred_username</c>, <c>email</c>, <c>name</c>. Keycloak's
/// <c>preferred_username</c> is often a bare login, so the mailbox address comes from
/// <c>email</c> and falls back to the username only when it is an address.
/// </remarks>
public sealed class HttpUserContext(IHttpContextAccessor accessor) : IUserContext
{
    public const string SubjectClaim = "sub";
    public const string UsernameClaim = "preferred_username";
    public const string EmailClaim = "email";
    public const string NameClaim = "name";

    private Principal? _principal;

    public Principal Principal => _principal ??= FromClaims(CurrentUser());

    public string BearerToken
    {
        get
        {
            var header = Context().Request.Headers[HeaderNames.Authorization].ToString();
            const string prefix = "Bearer ";
            return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? header[prefix.Length..].Trim()
                : throw new InvalidOperationException("The request carries no bearer token.");
        }
    }

    /// <summary>Builds a <see cref="Principal"/> from OIDC claims. Throws when the caller is not authenticated.</summary>
    public static Principal FromClaims(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (user.Identity?.IsAuthenticated != true)
        {
            throw new InvalidOperationException("The request is not authenticated.");
        }

        var sub = user.FindFirst(SubjectClaim)?.Value
            ?? throw new InvalidOperationException("The access token has no 'sub' claim.");
        var username = user.FindFirst(UsernameClaim)?.Value;
        var email = user.FindFirst(EmailClaim)?.Value;
        var mail = !string.IsNullOrWhiteSpace(email)
            ? email
            : username is not null && username.Contains('@', StringComparison.Ordinal) ? username : string.Empty;
        var upn = !string.IsNullOrWhiteSpace(username) ? username : mail;
        var displayName = user.FindFirst(NameClaim)?.Value ?? (string.IsNullOrEmpty(upn) ? sub : upn);

        return new Principal(sub, upn.ToLowerInvariant(), mail.ToLowerInvariant(), displayName);
    }

    private ClaimsPrincipal CurrentUser() => Context().User;

    private HttpContext Context() =>
        accessor.HttpContext ?? throw new InvalidOperationException("IUserContext used outside an HTTP request.");
}

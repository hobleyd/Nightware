namespace Nightware.Api.Auth;

/// <summary>JWT bearer validation settings (configuration section <c>Auth</c>).</summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>The OIDC issuer whose metadata and signing keys are trusted (Keycloak realm URL).</summary>
    public string? Authority { get; set; }

    /// <summary>The audience access tokens must carry (e.g. <c>nightware-api</c>). Empty disables the check.</summary>
    public string? Audience { get; set; }

    /// <summary>
    /// Where to fetch the OIDC metadata (and so the signing keys) when the public <see cref="Authority"/>
    /// does not resolve from inside the deployment, e.g.
    /// <c>http://keycloak:8080/realms/nightware/.well-known/openid-configuration</c>. Issuer validation
    /// still uses the <c>issuer</c> the metadata publishes (the public URL) plus <see cref="ValidIssuers"/>.
    /// </summary>
    public string? MetadataAddress { get; set; }

    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// Every Graph request's token must carry <c>email_verified: true</c>, since the <c>email</c> claim names the
    /// caller's mailbox (here and at Stalwart, through the exchanged token). Users created by the console, the
    /// realm import and the deployment are verified; turn this off only for an identity provider that never
    /// sends the claim.
    /// </summary>
    public bool RequireVerifiedEmail { get; set; } = true;

    /// <summary>Extra accepted <c>iss</c> values (e.g. the realm's internal and external URLs).</summary>
    public IList<string> ValidIssuers { get; } = [];
}

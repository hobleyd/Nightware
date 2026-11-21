namespace Nightware.Adapters.Directory.Core;

/// <summary>A bearer token issued by Keycloak and the instant it expires.</summary>
/// <param name="Value">The raw token. Never log it; <see cref="ToString"/> redacts it.</param>
public sealed record AccessToken(string Value, DateTimeOffset ExpiresAt)
{
    public override string ToString() => $"AccessToken *** (expires {ExpiresAt:O})";
}

/// <summary>An access token with the refresh token Keycloak issued beside it.</summary>
public sealed record TokenPair(AccessToken Access, string RefreshToken)
{
    public override string ToString() => $"TokenPair *** ({Access})";
}

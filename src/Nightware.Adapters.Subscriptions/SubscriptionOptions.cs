namespace Nightware.Adapters.Subscriptions;

/// <summary>
/// The <c>Subscriptions</c> section. Change notifications are on only when both <see cref="ConnectionString"/>
/// and <see cref="SealingKey"/> are set; otherwise <c>/subscriptions</c> answers 501.
/// </summary>
public sealed class SubscriptionOptions
{
    public const string SectionName = "Subscriptions";

    /// <summary>The subscription store's Postgres (its own database, not Keycloak's).</summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Hex, at least 32 bytes: the AES-256-GCM key that seals each subscription's refresh token. It lives in the
    /// environment, never in the database. Replacing it invalidates every stored credential (subscribers are
    /// asked to reauthorize).
    /// </summary>
    public string? SealingKey { get; set; }

    /// <summary>How often each subscription is polled for changes.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>How long a validation or notification POST may take (Graph allows 10 seconds).</summary>
    public TimeSpan EndpointTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Test only: exact <c>scheme://host:port</c> origins exempt from the address guard and the HTTPS rule, so an
    /// integration test can receive notifications on loopback. Never set by any deployment.
    /// </summary>
    public IList<string> TestOnlyAllowedOrigins { get; } = [];

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ConnectionString) && !string.IsNullOrWhiteSpace(SealingKey);
}

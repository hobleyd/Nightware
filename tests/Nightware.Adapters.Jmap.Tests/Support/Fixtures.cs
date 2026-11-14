namespace Nightware.Adapters.Jmap.Tests.Support;

/// <summary>JSON fixtures shaped like (or recorded from) Stalwart v1.1.</summary>
internal static class Fixtures
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    /// <summary>Recorded from Stalwart v1.1.0 (anonymous GET /jmap/session).</summary>
    public static string AnonymousSession => Read("stalwart-session-anonymous.json");

    /// <summary>The anonymous document's capabilities/URLs, plus accounts for alice (primary), bob (shared) and a read-only "reception" account.</summary>
    public static string AliceSession => Read("stalwart-session.json");

    public static string EmailGetResponse => Read("email-get-response.json");
}

namespace Nightware.Adapters.Directory.Tests.TestSupport;

/// <summary>Loads JSON fixtures recorded from (or shaped exactly like) Keycloak 26 responses.</summary>
internal static class Fixture
{
    public static string Load(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    /// <summary>A token-endpoint fixture with the placeholder replaced by <paramref name="accessToken"/>.</summary>
    public static string Token(string name, string accessToken) =>
        Load(name).Replace("{{access_token}}", accessToken, StringComparison.Ordinal);
}

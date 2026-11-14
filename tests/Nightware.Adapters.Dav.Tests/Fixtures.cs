namespace Nightware.Adapters.Dav.Tests;

/// <summary>Loads text fixtures copied next to the test assembly.</summary>
internal static class Fixture
{
    public static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", relativePath));
}

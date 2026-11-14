using Microsoft.AspNetCore.Http;

namespace Nightware.Api.Graph;

/// <summary>The Graph version roots the façade serves.</summary>
public static class GraphPaths
{
    public const string V1 = "/v1.0";
    public const string Beta = "/beta";

    public static IReadOnlyList<string> Versions { get; } = [V1, Beta];

    public static bool IsGraphRequest(PathString path) =>
        Versions.Any(v => path.StartsWithSegments(v, StringComparison.OrdinalIgnoreCase));

    /// <summary>The version root a request was made under (<c>/v1.0</c> or <c>/beta</c>), defaulting to v1.0.</summary>
    public static string VersionOf(PathString path) =>
        Versions.FirstOrDefault(v => path.StartsWithSegments(v, StringComparison.OrdinalIgnoreCase)) ?? V1;
}

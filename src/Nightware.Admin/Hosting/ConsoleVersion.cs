using System.Reflection;

namespace Nightware.Admin.Hosting;

/// <summary>
/// The console's release (<c>Version</c> in <c>Directory.Build.props</c>), as the top bar shows it. The build may add
/// <c>+&lt;commit&gt;</c> to the informational version; that part is left off.
/// </summary>
public static class ConsoleVersion
{
    public static string Current { get; } = Of(typeof(ConsoleVersion).Assembly);

    internal static string Of(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return informational?.Split('+')[0] is { Length: > 0 } version ? version : assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}

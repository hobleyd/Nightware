using System.Reflection;
using System.Text.RegularExpressions;

using Nightware.Admin.Hosting;

namespace Nightware.Admin.Tests.Hosting;

public sealed partial class ConsoleVersionTests
{
    [Fact]
    public void The_console_is_the_version_in_directory_build_props()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Directory.Build.props")))
        {
            root = root.Parent;
        }

        string props = File.ReadAllText(Path.Combine(root!.FullName, "Directory.Build.props"));

        ConsoleVersion.Current.ShouldBe(VersionElement().Match(props).Groups[1].Value);
        ConsoleVersion.Current.ShouldMatch(@"^\d+\.\d+\.\d+$");
    }

    [Fact]
    public void A_build_commit_after_the_version_is_left_off()
    {
        AssemblyName name = new("Versioned");
        var builder = System.Reflection.Emit.AssemblyBuilder.DefineDynamicAssembly(name, System.Reflection.Emit.AssemblyBuilderAccess.Run);
        builder.SetCustomAttribute(new System.Reflection.Emit.CustomAttributeBuilder(
            typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!, ["2.3.4+0123abcd"]));

        ConsoleVersion.Of(builder).ShouldBe("2.3.4");
    }

    [GeneratedRegex("<Version>([^<]+)</Version>")]
    private static partial Regex VersionElement();
}

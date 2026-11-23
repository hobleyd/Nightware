using System.Reflection;
using System.Reflection.Emit;
using Nightware.Adapters.Dav.Core.Http;

namespace Nightware.Adapters.Dav.Tests.Core.Http;

public sealed class NextcloudClientsTests
{
    [Fact]
    public void User_agent_names_nightware_and_its_release()
    {
        NextcloudClients.UserAgent.Product!.Name.ShouldBe("Nightware");
        NextcloudClients.UserAgent.Product.Version.ShouldNotBeNullOrEmpty();
        NextcloudClients.UserAgent.Product.Version.ShouldNotContain("+");
    }

    [Fact]
    public void Version_drops_the_commit_suffix()
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("V") { Version = new Version(9, 8, 7, 6) }, AssemblyBuilderAccess.Run);
        NextcloudClients.VersionOf(assembly).ShouldBe("9.8.7");

        assembly.SetCustomAttribute(new CustomAttributeBuilder(
            typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!, ["1.2.3+abcdef"]));
        NextcloudClients.VersionOf(assembly).ShouldBe("1.2.3");
    }
}

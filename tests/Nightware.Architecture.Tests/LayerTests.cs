using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnitV3;

using static ArchUnitNET.Fluent.ArchRuleDefinition;

using ArchArchitecture = ArchUnitNET.Domain.Architecture;

namespace Nightware.Architecture.Tests;

/// <summary>Clean Architecture layer rules (architecture.md "Layers"), enforced on the compiled assemblies.</summary>
public sealed class LayerTests
{
    private const string Domain = "Nightware.Domain";
    private const string Application = "Nightware.Application";
    private const string Api = "Nightware.Api";
    private const string Composition = "Nightware.Api.Composition";
    private const string Features = "Nightware.Api.Features";
    private const string AnyAdapter = @"^Nightware\.Adapters(\..*)?$";

    private static readonly string[] Adapters = ["Nightware.Adapters.Jmap", "Nightware.Adapters.Dav", "Nightware.Adapters.Directory"];

    private static readonly ArchArchitecture Architecture = new ArchLoader()
        .LoadAssemblies(
            typeof(Nightware.Domain.Kernel.Result).Assembly,
            typeof(Nightware.Application.ApplicationServiceCollectionExtensions).Assembly,
            typeof(Nightware.Adapters.Jmap.JmapAdapterServiceCollectionExtensions).Assembly,
            typeof(Nightware.Adapters.Dav.DavAdapterServiceCollectionExtensions).Assembly,
            typeof(Nightware.Adapters.Directory.DirectoryAdapterServiceCollectionExtensions).Assembly,
            typeof(Nightware.Api.Composition.NightwareComposition).Assembly)
        .Build();

    private static ArchUnitNET.Fluent.Syntax.Elements.Types.GivenTypesConjunction InNamespace(string root) => Types().That().ResideInNamespaceOrBelow(root);

    [Fact]
    public void Domain_depends_on_nothing_else_in_the_solution()
    {
        Types().That().ResideInNamespaceOrBelow(Domain).Should()
            .NotDependOnAny(InNamespace(Application))
            .AndShould().NotDependOnAny(Types().That().ResideInNamespaceMatching(AnyAdapter))
            .AndShould().NotDependOnAny(InNamespace(Api))
            .AndShould().NotDependOnAny(InNamespace("Microsoft"))
            .Because("Domain is the kernel: BCL only (ADR 0001 §15)")
            .Check(Architecture);
    }

    [Fact]
    public void Application_depends_only_on_domain()
    {
        Types().That().ResideInNamespaceOrBelow(Application).Should()
            .NotDependOnAny(Types().That().ResideInNamespaceMatching(AnyAdapter))
            .AndShould().NotDependOnAny(InNamespace(Api))
            .Because("ports point inward; adapters implement them")
            .Check(Architecture);
    }

    [Fact]
    public void Domain_and_application_use_no_aspnet_types()
    {
        Types().That().ResideInNamespaceOrBelow(Domain).Or().ResideInNamespaceOrBelow(Application).Should()
            .NotDependOnAny(InNamespace("Microsoft.AspNetCore"))
            .AndShould().NotDependOnAny(InNamespace("System.Net.Http"))
            .Because("HTTP is an adapter and Api concern")
            .Check(Architecture);
    }

    [Theory]
    [InlineData("Nightware.Adapters.Jmap")]
    [InlineData("Nightware.Adapters.Dav")]
    [InlineData("Nightware.Adapters.Directory")]
    public void Adapters_depend_neither_on_the_api_nor_on_each_other(string adapter)
    {
        IArchRule rule = Types().That().ResideInNamespaceOrBelow(adapter).Should().NotDependOnAny(InNamespace(Api));
        foreach (var other in Adapters.Where(a => a != adapter))
        {
            rule = rule.And(Types().That().ResideInNamespaceOrBelow(adapter).Should().NotDependOnAny(InNamespace(other)));
        }

        rule.Check(Architecture);
    }

    [Fact]
    public void Feature_endpoints_do_not_depend_on_adapters()
    {
        Types().That().ResideInNamespaceOrBelow(Features).Should()
            .NotDependOnAny(Types().That().ResideInNamespaceMatching(AnyAdapter))
            .Because("endpoint code talks to Application ports and use cases only")
            .Check(Architecture);
    }

    [Fact]
    public void Only_the_composition_root_depends_on_adapters()
    {
        Types().That().ResideInNamespaceOrBelow(Api).And().DoNotResideInNamespaceOrBelow(Composition).Should()
            .NotDependOnAny(Types().That().ResideInNamespaceMatching(AnyAdapter))
            .Because("architecture.md: the Api references adapters only in Composition/")
            .Check(Architecture);
    }

    [Fact]
    public void The_rules_can_see_dependencies_at_all()
    {
        // Guards against a loader/namespace mistake that would make every NotDependOnAny rule pass vacuously.
        Types().That().HaveFullName("Nightware.Api.Composition.NightwareComposition").Should()
            .DependOnAny(Types().That().ResideInNamespaceMatching(AnyAdapter))
            .AndShould().DependOnAny(InNamespace(Application))
            .Check(Architecture);
    }
}

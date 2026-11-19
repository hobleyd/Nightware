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
    private const string Hosting = "Nightware.Hosting";
    private const string Composition = "Nightware.Api.Composition";
    private const string Features = "Nightware.Api.Features";
    private const string Admin = "Nightware.Admin";
    private const string AdminComposition = "Nightware.Admin.Composition";
    private const string Administration = "Nightware.Application.Administration";
    private const string AnyAdministrationAdapter = @"^Nightware\.Adapters\.[^.]+\.Administration(\..*)?$";
    private const string AnyAdapter = @"^Nightware\.Adapters(\..*)?$";

    private static readonly string[] Adapters = ["Nightware.Adapters.Jmap", "Nightware.Adapters.Dav", "Nightware.Adapters.Directory", "Nightware.Adapters.Cloudflare", "Nightware.Adapters.DnsHosts"];

    private static readonly ArchArchitecture Architecture = new ArchLoader()
        .LoadAssemblies(
            typeof(Nightware.Domain.Kernel.Result).Assembly,
            typeof(Nightware.Application.ApplicationServiceCollectionExtensions).Assembly,
            typeof(Nightware.Adapters.Jmap.JmapAdapterServiceCollectionExtensions).Assembly,
            typeof(Nightware.Adapters.Dav.DavAdapterServiceCollectionExtensions).Assembly,
            typeof(Nightware.Adapters.Directory.DirectoryAdapterServiceCollectionExtensions).Assembly,
            typeof(Nightware.Adapters.Cloudflare.Administration.CloudflareAdministrationServiceCollectionExtensions).Assembly,
            typeof(Nightware.Adapters.DnsHosts.Administration.DnsHostsAdministrationServiceCollectionExtensions).Assembly,
            typeof(Nightware.Api.Composition.NightwareComposition).Assembly,
            typeof(Nightware.Hosting.HostingDefaultsServiceCollectionExtensions).Assembly,
            typeof(Nightware.Admin.Composition.AdminComposition).Assembly)
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
    [InlineData("Nightware.Adapters.Cloudflare")]
    [InlineData("Nightware.Adapters.DnsHosts")]
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
    public void Hosting_depends_on_no_other_nightware_namespace()
    {
        Types().That().ResideInNamespaceOrBelow(Hosting).Should()
            .NotDependOnAny(Types().That().ResideInNamespaceMatching(@"^Nightware(\.(?!Hosting(\.|$)).*)?$"))
            .Because("ADR 0002 §D2: shared hosting code is referenced by both hosts and references neither")
            .Check(Architecture);
    }

    [Fact]
    public void Nothing_inward_depends_on_hosting()
    {
        Types().That().ResideInNamespaceOrBelow(Domain).Or().ResideInNamespaceOrBelow(Application)
            .Or().ResideInNamespaceMatching(AnyAdapter).Should()
            .NotDependOnAny(InNamespace(Hosting))
            .Because("hosting code is a host concern")
            .Check(Architecture);
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
            .AndShould().DependOnAny(InNamespace(Hosting))
            .Check(Architecture);
    }

    [Fact]
    public void The_console_and_the_graph_facade_never_depend_on_each_other()
    {
        Types().That().ResideInNamespaceOrBelow(Admin).Should().NotDependOnAny(InNamespace(Api))
            .Because("ADR 0002 D2: the console is a separate host")
            .Check(Architecture);
        Types().That().ResideInNamespaceOrBelow(Api).Should()
            .NotDependOnAny(InNamespace(Admin))
            .AndShould().NotDependOnAny(InNamespace(Administration))
            .AndShould().NotDependOnAny(Types().That().ResideInNamespaceMatching(AnyAdministrationAdapter))
            .Because("ADR 0002 D2: the Graph façade has no administration surface")
            .Check(Architecture);
    }

    [Fact]
    public void Only_the_console_composition_root_depends_on_adapters()
    {
        Types().That().ResideInNamespaceOrBelow(Admin).And().DoNotResideInNamespaceOrBelow(AdminComposition).Should()
            .NotDependOnAny(Types().That().ResideInNamespaceMatching(AnyAdapter))
            .Because("console pages talk to Application use cases only")
            .Check(Architecture);
    }

    [Fact]
    public void The_console_rules_can_see_dependencies_at_all()
    {
        Types().That().HaveFullName("Nightware.Admin.Composition.AdminComposition").Should()
            .DependOnAny(Types().That().ResideInNamespaceMatching(AnyAdministrationAdapter))
            .AndShould().DependOnAny(InNamespace(Administration))
            .Check(Architecture);
    }
}

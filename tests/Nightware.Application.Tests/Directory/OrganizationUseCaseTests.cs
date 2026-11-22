using Nightware.Application.Directory;
using Nightware.Domain.Directory;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Directory;

/// <summary><see cref="ListOrganizations"/> and <see cref="GetOrganization"/>.</summary>
public sealed class OrganizationUseCaseTests
{
    private static readonly DirectoryOrganization Acme = new("org-1", "Acme", ["acme.example"]);

    private readonly IOrganizationDirectory _organizations = Substitute.For<IOrganizationDirectory>();

    public OrganizationUseCaseTests() =>
        _organizations.ListAsync(Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<DirectoryOrganization>>.Success([Acme]));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Lists_the_callers_organizations()
    {
        (await new ListOrganizations(_organizations).ExecuteAsync(Ct)).Value.ShouldBe([Acme]);
    }

    [Fact]
    public async Task Gets_one_of_them_and_any_other_id_is_Graph_s_404()
    {
        var useCase = new GetOrganization(_organizations);

        (await useCase.ExecuteAsync("ORG-1", Ct)).Value.ShouldBe(Acme);
        (await useCase.ExecuteAsync("org-2", Ct)).Error!.Code.ShouldBe("Request_ResourceNotFound");
    }

    [Fact]
    public async Task A_failed_read_passes_through()
    {
        _organizations.ListAsync(Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("down"));

        (await new GetOrganization(_organizations).ExecuteAsync("org-1", Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }
}

using Nightware.Adapters.Directory.Core.Admin;

namespace Nightware.Adapters.Directory.Tests.Core.Admin;

public sealed class KeycloakUserQueryTests
{
    [Fact]
    public void Empty_query_has_no_query_string() => new KeycloakUserQuery().ToQueryString(includePaging: true).ShouldBeEmpty();

    [Fact]
    public void Encodes_every_parameter()
    {
        KeycloakUserQuery query = new()
        {
            Search = "a b&c",
            Username = "u",
            Email = "e+1@x",
            FirstName = "F",
            LastName = "L",
            Exact = false,
            Enabled = true,
            First = 0,
            Max = 50,
            BriefRepresentation = true,
        };

        query.ToQueryString(includePaging: true).ShouldBe(
            "?search=a%20b%26c&username=u&email=e%2B1%40x&firstName=F&lastName=L&exact=false&enabled=true&first=0&max=50&briefRepresentation=true");
    }

    [Fact]
    public void Paging_can_be_omitted() =>
        new KeycloakUserQuery { Search = "x", First = 1, Max = 2, BriefRepresentation = true }.ToQueryString(includePaging: false).ShouldBe("?search=x");

    [Fact]
    public void Factories_build_exact_lookups()
    {
        KeycloakUserQuery.ByEmail("a@b").ShouldBe(new KeycloakUserQuery { Email = "a@b", Exact = true });
        KeycloakUserQuery.ByUsername("bob").ShouldBe(new KeycloakUserQuery { Username = "bob", Exact = true });
    }
}

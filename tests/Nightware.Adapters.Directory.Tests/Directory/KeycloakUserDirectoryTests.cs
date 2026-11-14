using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using Nightware.Adapters.Directory.Tests.TestSupport;
using Nightware.Application.Common.Query;
using Nightware.Application.Directory;
using Nightware.Domain.Kernel;

using WireMock.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Nightware.Adapters.Directory.Tests.Directory;

public sealed class KeycloakUserDirectoryTests : IDisposable
{
    private const string AliceId = "5d2c6a4e-2f0b-4a8e-9f3c-1b7e2d9a6c11";
    private readonly KeycloakStub _keycloak = new();

    public KeycloakUserDirectoryTests() => _keycloak.GivenServiceAccountToken();

    public void Dispose() => _keycloak.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AdapterHost Host() => AdapterHost.Create(_keycloak.Url, new Dictionary<string, string?> { ["Directory:MaxRetryAttempts"] = "0" });

    /// <summary>The port as a request would get it: scoped, like the Api's MailboxResolver that consumes it.</summary>
    private static IUserDirectory Users(AdapterHost host) => host.Provider.CreateScope().ServiceProvider.GetRequiredService<IUserDirectory>();

    private static string Page(params int[] indexes)
    {
        var all = JsonElement.Parse(Fixture.Load("users-directory-page.json")).EnumerateArray().ToList();
        return "[" + string.Join(',', indexes.Select(i => all[i].GetRawText())) + "]";
    }

    private void GivenUsers(string body, Func<IDictionary<string, WireMock.Types.WireMockList<string>>?, bool>? match = null, int priority = 10) =>
        _keycloak.Server.Given(Request.Create().WithPath(KeycloakStub.AdminPath + "/users").UsingGet()
                .WithParam(q => match?.Invoke(q) ?? true))
            .AtPriority(priority)
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(body));

    private void GivenAdmin(string path, int status, string body = "") =>
        _keycloak.Server.Given(Request.Create().WithPath(KeycloakStub.AdminPath + path).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body));

    private static bool Has(IDictionary<string, WireMock.Types.WireMockList<string>>? query, string name, string value) =>
        query is not null && query.TryGetValue(name, out var values) && values.Contains(value);

    private List<ILogEntry> UserQueries() => [.. _keycloak.AdminRequests.Where(static e => e.RequestMessage!.Path.EndsWith("/users", StringComparison.Ordinal))];

    [Fact]
    public async Task Maps_a_keycloak_user_with_its_profile_attributes()
    {
        GivenUsers(Page(0), q => Has(q, "email", "alice@nightware.localhost"));
        await using var host = Host();

        var alice = (await Users(host).GetAsync("alice@nightware.localhost", Ct)).Value;

        alice.Id.ShouldBe(AliceId);
        alice.DisplayName.ShouldBe("Alice Anderson");
        alice.UserPrincipalName.ShouldBe("alice@nightware.localhost", "a bare login is not a UPN");
        alice.Mail.ShouldBe("alice@nightware.localhost");
        alice.GivenName.ShouldBe("Alice");
        alice.Surname.ShouldBe("Anderson");
        alice.JobTitle.ShouldBe("CTO");
        alice.Department.ShouldBe("Engineering");
        alice.CompanyName.ShouldBe("Nightware Pty Ltd");
        alice.OfficeLocation.ShouldBe("Sydney");
        alice.MobilePhone.ShouldBe("+61 400 000 001");
        alice.BusinessPhones.ShouldBe(["+61 2 9000 0001", "+61 2 9000 0002"]);
        alice.Aliases.ShouldBe(["a.anderson@nightware.localhost"], "blank, duplicate and primary entries are dropped");
        var query = UserQueries().ShouldHaveSingleItem().RequestMessage!.Query!;
        query["exact"].ShouldBe(["true"]);
    }

    [Fact]
    public async Task An_address_that_is_no_email_is_tried_as_a_username()
    {
        GivenUsers("[]", q => Has(q, "email", "carol@corp.example.com"));
        GivenUsers(Fixture.Load("user-alice.json").Replace("\"username\": \"alice\"", "\"username\": \"carol@corp.example.com\"", StringComparison.Ordinal)
                .Insert(0, "[") + "]", q => Has(q, "username", "carol@corp.example.com"));
        await using var host = Host();

        var user = (await Users(host).GetAsync("carol@corp.example.com", Ct)).Value;

        user.UserPrincipalName.ShouldBe("carol@corp.example.com", "an address-shaped username is the UPN");
        user.Mail.ShouldBe("alice@nightware.localhost");
    }

    [Fact]
    public async Task A_uuid_is_looked_up_as_a_directory_id()
    {
        GivenAdmin($"/users/{AliceId}", 200, Fixture.Load("user-alice.json"));
        await using var host = Host();

        var alice = (await Users(host).GetAsync(AliceId, Ct)).Value;

        alice.Id.ShouldBe(AliceId);
        alice.UserPrincipalName.ShouldBe("alice@nightware.localhost", "a bare login is not a UPN");
        UserQueries().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_bare_name_is_looked_up_as_a_username()
    {
        GivenUsers(Page(2), q => Has(q, "username", "bob"));
        await using var host = Host();

        (await Users(host).GetAsync("bob", Ct)).Value.DisplayName.ShouldBe("Bob Brown");
    }

    [Fact]
    public async Task Service_accounts_are_not_people()
    {
        GivenUsers(Page(1), q => Has(q, "username", "service-account-nightware-api"));
        await using var host = Host();

        (await Users(host).GetAsync("service-account-nightware-api", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task An_unknown_user_is_not_found()
    {
        GivenUsers("[]");
        GivenAdmin("/users/00000000-0000-0000-0000-000000000000", 404, """{"error":"User not found"}""");
        await using var host = Host();
        var directory = Users(host);

        (await directory.GetAsync("nobody@example.com", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await directory.GetAsync("00000000-0000-0000-0000-000000000000", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await directory.GetAsync("nobody", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Theory]
    [InlineData(403, ErrorKind.Forbidden)]
    [InlineData(500, ErrorKind.BackendUnavailable)]
    [InlineData(401, ErrorKind.BackendUnavailable)]
    public async Task Admin_api_failures_keep_their_kind_and_are_never_unauthorized(int status, ErrorKind kind)
    {
        _keycloak.Server.Given(Request.Create().WithPath(KeycloakStub.AdminPath + "/users").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(status));
        await using var host = Host();

        (await Users(host).GetAsync("bob", Ct)).Error!.Kind.ShouldBe(kind);
    }

    [Fact]
    public async Task Lists_enabled_users_with_full_representations_asking_one_extra_row()
    {
        GivenUsers(Page(0, 1, 2));
        await using var host = Host();

        var page = (await Users(host).ListAsync(new ItemQuery { Top = 2 }, Ct)).Value;

        page.Items.Select(static u => u.Mail).ShouldBe(["alice@nightware.localhost"], "the service account is filtered; bob is the look-ahead row");
        page.SkipToken.ShouldBe("2", "the offset advances by the rows Keycloak returned, not the rows kept");
        var query = UserQueries().ShouldHaveSingleItem().RequestMessage!.Query!;
        query["first"].ShouldBe(["0"]);
        query["max"].ShouldBe(["3"]);
        query["enabled"].ShouldBe(["true"]);
        query["briefRepresentation"].ShouldBe(["false"]);
    }

    [Fact]
    public async Task The_skiptoken_is_the_next_offset_and_the_last_page_has_none()
    {
        GivenUsers(Page(2), q => Has(q, "first", "2"));
        await using var host = Host();

        var page = (await Users(host).ListAsync(new ItemQuery { Top = 2, SkipToken = "2" }, Ct)).Value;

        page.Items.ShouldHaveSingleItem().DisplayName.ShouldBe("Bob Brown");
        page.SkipToken.ShouldBeNull();
    }

    [Fact]
    public async Task Pages_default_to_graphs_size()
    {
        GivenUsers("[]");
        await using var host = Host();

        (await Users(host).ListAsync(ItemQuery.Default, Ct)).Value.Items.ShouldBeEmpty();

        UserQueries().ShouldHaveSingleItem().RequestMessage!.Query!["max"].ShouldBe(["101"]);
    }

    public static TheoryData<ItemQuery, string> Unsupported => new()
    {
        { new ItemQuery { Filter = new FilterNode.Compare("mail", CompareOp.Eq, new FilterValue.Text("x")) }, "$filter" },
        { new ItemQuery { Search = new SearchQuery([]) }, "$search" },
        { new ItemQuery { OrderBy = [new OrderBy("displayName", Nightware.Application.Common.Query.SortDirection.Ascending)] }, "$orderby" },
        { new ItemQuery { Skip = 10 }, "$skip" },
        { new ItemQuery { Expand = [new ExpandClause("manager")] }, "$expand" },
        { new ItemQuery { SkipToken = "-1" }, "$skiptoken" },
        { new ItemQuery { SkipToken = "abc" }, "$skiptoken" },
    };

    [Theory]
    [MemberData(nameof(Unsupported))]
    public async Task Rejects_what_keycloak_cannot_answer_without_asking_it(ItemQuery query, string option)
    {
        await using var host = Host();

        var error = (await Users(host).ListAsync(query, Ct)).Error!;

        error.Kind.ShouldBe(ErrorKind.Validation);
        error.Target.ShouldBe(option);
        UserQueries().ShouldBeEmpty();
    }

    [Fact]
    public async Task A_listing_failure_passes_through()
    {
        _keycloak.Server.Given(Request.Create().WithPath(KeycloakStub.AdminPath + "/users").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(429).WithHeader("Retry-After", "7"));
        await using var host = Host();

        var error = (await Users(host).ListAsync(ItemQuery.Default, Ct)).Error!;

        error.Kind.ShouldBe(ErrorKind.Throttled);
        error.RetryAfterSeconds.ShouldBe(7);
    }

    [Fact]
    public async Task A_user_without_names_is_displayed_by_login_then_address()
    {
        GivenUsers("""[{"id":"x1","username":"nameless","email":"n@example.com","enabled":true}]""", q => Has(q, "username", "nameless"));
        await using var host = Host();

        var user = (await Users(host).GetAsync("nameless", Ct)).Value;

        user.DisplayName.ShouldBe("nameless");
        user.UserPrincipalName.ShouldBe("n@example.com");
        user.BusinessPhones.ShouldBeEmpty();
        user.Aliases.ShouldBeEmpty();
    }
}

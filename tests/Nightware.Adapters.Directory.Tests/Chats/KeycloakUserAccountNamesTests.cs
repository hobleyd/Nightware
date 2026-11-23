using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using Nightware.Adapters.Directory.Tests.TestSupport;
using Nightware.Application.Directory;
using Nightware.Domain.Kernel;

using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Nightware.Adapters.Directory.Tests.Chats;

public sealed class KeycloakUserAccountNamesTests : IDisposable
{
    private const string AliceId = "5d2c6a4e-2f0b-4a8e-9f3c-1b7e2d9a6c11";
    private readonly KeycloakStub _keycloak = new();

    public KeycloakUserAccountNamesTests() => _keycloak.GivenServiceAccountToken();

    public void Dispose() => _keycloak.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private AdapterHost Host() => AdapterHost.Create(_keycloak.Url, new Dictionary<string, string?> { ["Directory:MaxRetryAttempts"] = "0" });

    private static IUserAccountNames Names(AdapterHost host) => host.Provider.CreateScope().ServiceProvider.GetRequiredService<IUserAccountNames>();

    private int AdminRequests() => _keycloak.Server.LogEntries.Count(static e => e.RequestMessage!.Path.StartsWith(KeycloakStub.AdminPath, StringComparison.Ordinal));

    private void GivenAlice() =>
        _keycloak.Server.Given(Request.Create().WithPath(KeycloakStub.AdminPath + "/users/" + AliceId).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(Fixture.Load("user-alice.json")));

    private void GivenUsername(string username, string body) =>
        _keycloak.Server.Given(Request.Create().WithPath(KeycloakStub.AdminPath + "/users").UsingGet().WithParam("username", username))
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(body));

    [Fact]
    public async Task A_directory_id_maps_to_the_username_once_per_lifetime()
    {
        GivenAlice();
        await using var host = Host();

        (await Names(host).AccountNameOfAsync(AliceId, Ct)).Value.ShouldBe("alice");
        var requests = AdminRequests();
        (await Names(host).AccountNameOfAsync(AliceId.ToUpperInvariant(), Ct)).Value.ShouldBe("alice");
        AdminRequests().ShouldBe(requests, "the second lookup is memoised");

        // The forward lookup also primes the reverse one.
        (await Names(host).UserOfAccountAsync("alice", Ct)).Value.Id.ShouldBe(AliceId);
        AdminRequests().ShouldBe(requests);
    }

    [Fact]
    public async Task An_account_name_maps_to_the_person()
    {
        GivenUsername("alice", "[" + Fixture.Load("user-alice.json") + "]");
        await using var host = Host();

        var alice = (await Names(host).UserOfAccountAsync("alice", Ct)).Value;

        alice.Id.ShouldBe(AliceId);
        alice.DisplayName.ShouldBe("Alice Anderson");
    }

    [Fact]
    public async Task Unknown_people_and_service_accounts_are_not_found()
    {
        GivenUsername("ghost", "[]");
        GivenUsername("service-account-nightware-api",
            """[{"id":"s1","username":"service-account-nightware-api","enabled":true,"serviceAccountClientId":"nightware-api"}]""");
        await using var host = Host();

        (await Names(host).UserOfAccountAsync("ghost", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await Names(host).UserOfAccountAsync("service-account-nightware-api", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        (await Names(host).AccountNameOfAsync("ghost", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task A_user_without_a_username_has_no_account_name()
    {
        var nameless = JsonSerializer.Serialize(new { id = AliceId, email = "n@example.com", enabled = true, firstName = "N", lastName = "N" });
        _keycloak.Server.Given(Request.Create().WithPath(KeycloakStub.AdminPath + "/users/" + AliceId).UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody(nameless));
        await using var host = Host();

        (await Names(host).AccountNameOfAsync(AliceId, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }
}

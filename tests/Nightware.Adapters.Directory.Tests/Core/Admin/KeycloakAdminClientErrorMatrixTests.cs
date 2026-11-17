using Nightware.Adapters.Directory.Core.Admin;
using Nightware.Adapters.Directory.Tests.TestSupport;
using Nightware.Domain.Kernel;

using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Nightware.Adapters.Directory.Tests.Core.Admin;

/// <summary>Every new admin call maps 400/403/404/409/429/5xx the same way and refreshes the token once on 401.</summary>
public sealed class KeycloakAdminClientErrorMatrixTests : IDisposable
{
    private const string Org = "org-1";
    private const string User = "user-1";
    private const string Group = "group-1";
    private static readonly KeycloakRole Role = new() { Id = "r1", Name = "nightware-global-admin" };
    private readonly KeycloakStub _keycloak = new();

    public KeycloakAdminClientErrorMatrixTests() => _keycloak.GivenServiceAccountToken();

    public void Dispose() => _keycloak.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>name → (method, path, call, 409 is "already done")</summary>
    private static readonly Dictionary<string, (string Method, string Path, Func<IKeycloakAdminClient, Task<Error?>> Call, bool ConflictIsSuccess)> Operations = new()
    {
        ["create-user"] = ("POST", "/users", static c => Err(c.CreateUserAsync(new KeycloakNewUser { Username = "u", Email = "u@x.example" }, Ct)), false),
        ["logout"] = ("POST", $"/users/{User}/logout", static c => Err(c.LogoutUserAsync(User, Ct)), false),
        ["execute-actions-email"] = ("PUT", $"/users/{User}/execute-actions-email", static c => Err(c.ExecuteActionsEmailAsync(User, ["UPDATE_PASSWORD"], "nightware-admin", TimeSpan.FromHours(1), Ct)), false),
        ["reset-password"] = ("PUT", $"/users/{User}/reset-password", static c => Err(c.ResetPasswordAsync(User, "pw-123456789", true, Ct)), false),
        ["get-role"] = ("GET", "/roles/nightware-global-admin", static c => Err(c.GetRealmRoleAsync("nightware-global-admin", Ct)), false),
        ["role-users"] = ("GET", "/roles/nightware-global-admin/users", static c => Err(c.ListRealmRoleUsersAsync("nightware-global-admin", Ct)), false),
        ["user-roles"] = ("GET", $"/users/{User}/role-mappings/realm", static c => Err(c.GetUserRealmRolesAsync(User, Ct)), false),
        ["user-effective-roles"] = ("GET", $"/users/{User}/role-mappings/realm/composite", static c => Err(c.GetEffectiveUserRealmRolesAsync(User, Ct)), false),
        ["add-user-roles"] = ("POST", $"/users/{User}/role-mappings/realm", static c => Err(c.AddUserRealmRolesAsync(User, [Role], Ct)), false),
        ["remove-user-roles"] = ("DELETE", $"/users/{User}/role-mappings/realm", static c => Err(c.RemoveUserRealmRolesAsync(User, [Role], Ct)), false),
        ["list-orgs"] = ("GET", "/organizations", static c => Err(c.ListOrganizationsAsync(Ct)), false),
        ["search-orgs"] = ("GET", "/organizations", static c => Err(c.SearchOrganizationsAsync("x.example", true, Ct)), false),
        ["get-org"] = ("GET", $"/organizations/{Org}", static c => Err(c.GetOrganizationAsync(Org, Ct)), false),
        ["create-org"] = ("POST", "/organizations", static c => Err(c.CreateOrganizationAsync(new KeycloakOrganization { Name = "X" }, Ct)), false),
        ["org-members"] = ("GET", $"/organizations/{Org}/members", static c => Err(c.ListOrganizationMembersAsync(Org, Ct)), false),
        ["add-org-member"] = ("POST", $"/organizations/{Org}/members", static c => Err(c.AddOrganizationMemberAsync(Org, User, Ct)), true),
        ["member-orgs"] = ("GET", $"/organizations/members/{User}/organizations", static c => Err(c.ListMemberOrganizationsAsync(User, Ct)), false),
        ["member-groups"] = ("GET", $"/organizations/{Org}/members/{User}/groups", static c => Err(c.ListMemberOrganizationGroupsAsync(Org, User, Ct)), false),
        ["top-level-groups"] = ("GET", $"/organizations/{Org}/groups", static c => Err(c.ListOrganizationTopLevelGroupsAsync(Org, Ct)), false),
        ["create-group"] = ("POST", $"/organizations/{Org}/groups", static c => Err(c.CreateOrganizationGroupAsync(Org, "admins", Ct)), false),
        ["group-members"] = ("GET", $"/organizations/{Org}/groups/{Group}/members", static c => Err(c.ListOrganizationGroupMembersAsync(Org, Group, Ct)), false),
        ["join-group"] = ("PUT", $"/organizations/{Org}/groups/{Group}/members/{User}", static c => Err(c.AddOrganizationGroupMemberAsync(Org, Group, User, Ct)), true),
        ["leave-group"] = ("DELETE", $"/organizations/{Org}/groups/{Group}/members/{User}", static c => Err(c.RemoveOrganizationGroupMemberAsync(Org, Group, User, Ct)), false),
        ["group-roles"] = ("GET", $"/organizations/{Org}/groups/{Group}/role-mappings/realm", static c => Err(c.GetOrganizationGroupRealmRolesAsync(Org, Group, Ct)), false),
        ["add-group-roles"] = ("POST", $"/organizations/{Org}/groups/{Group}/role-mappings/realm", static c => Err(c.AddOrganizationGroupRealmRolesAsync(Org, Group, [Role], Ct)), false),
    };

    public static TheoryData<string, int> Cases()
    {
        TheoryData<string, int> data = [];
        foreach (string operation in Operations.Keys)
        {
            foreach (int status in new[] { 400, 403, 404, 409, 429, 500, 503 })
            {
                data.Add(operation, status);
            }
        }

        return data;
    }

    public static TheoryData<string> Names() => [.. Operations.Keys];

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Maps_each_error_status(string operation, int status)
    {
        (string method, string path, Func<IKeycloakAdminClient, Task<Error?>> call, bool conflictIsSuccess) = Operations[operation];
        _keycloak.GivenAdmin(path, status, """{"errorMessage":"nope"}""", method: method);
        await using AdapterHost host = AdapterHost.CreateConsole(_keycloak.Url, new Dictionary<string, string?> { ["Directory:MaxRetryAttempts"] = "0" });

        Error? error = await call(host.Get<IKeycloakAdminClient>());

        ErrorKind? expected = status switch
        {
            400 => ErrorKind.Validation,
            403 => ErrorKind.Forbidden,
            404 => ErrorKind.NotFound,
            409 when conflictIsSuccess => null,
            409 => ErrorKind.Conflict,
            429 => ErrorKind.Throttled,
            _ => ErrorKind.BackendUnavailable,
        };
        ((ErrorKind?)error?.Kind).ShouldBe(expected);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task A_401_refreshes_the_token_and_retries_once(string operation)
    {
        (string method, string path, Func<IKeycloakAdminClient, Task<Error?>> call, _) = Operations[operation];
        _keycloak.Server.Given(Request.Create().WithPath(KeycloakStub.AdminPath + path).UsingMethod(method).WithHeader("Authorization", "Bearer sa-token-1"))
            .AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(401));
        _keycloak.GivenAdmin(path, 401, method: method, priority: 20); // second token too: still 401
        await using AdapterHost host = AdapterHost.CreateConsole(_keycloak.Url);

        Error? error = await call(host.Get<IKeycloakAdminClient>());

        error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        _keycloak.ClientCredentialsHits.ShouldBe(2);
        _keycloak.Admin(method, path).Select(static r => r.Headers!["Authorization"].Single()).ShouldBe(["Bearer sa-token-1", "Bearer sa-token-2"]);
    }

    [Fact]
    public async Task Throttled_writes_carry_retry_after()
    {
        _keycloak.Server.Given(Request.Create().WithPath($"{KeycloakStub.AdminPath}/organizations/{Org}/members").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(429).WithHeader("Retry-After", "7"));
        await using AdapterHost host = AdapterHost.CreateConsole(_keycloak.Url);

        Error? error = await Err(host.Get<IKeycloakAdminClient>().AddOrganizationMemberAsync(Org, User, Ct));

        error!.Kind.ShouldBe(ErrorKind.Throttled);
        error.RetryAfterSeconds.ShouldBe(7);
    }

    private static async Task<Error?> Err<T>(Task<Result<T>> call) => (await call).Error;

    private static async Task<Error?> Err(Task<Result> call) => (await call).Error;
}

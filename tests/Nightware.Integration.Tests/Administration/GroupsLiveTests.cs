using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Nightware.Adapters.Dav.Administration;
using Nightware.Adapters.Directory;
using Nightware.Adapters.Directory.Administration;
using Nightware.Adapters.Jmap.Administration;
using Nightware.Application.Administration;
using Nightware.Application.Administration.Groups;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;
using Nightware.Integration.Tests.Support;

namespace Nightware.Integration.Tests.Administration;

/// <summary>
/// Phase 9 against the running dev stack: the console's group use cases over the real Keycloak, Stalwart and
/// Nextcloud adapters, checked in each backend directly. Opt-in (<c>NIGHTWARE_INTEGRATION=1</c>). Stalwart is
/// reached as its recovery administrator from outside the Docker network, where the console's own key does not
/// work (it is bound to the console container's address).
/// </summary>
public sealed class GroupsLiveTests : IAsyncDisposable
{
    private readonly DevStack? _stack;
    private readonly ServiceProvider? _services;

    public GroupsLiveTests()
    {
        if (!DevStack.Enabled)
        {
            return;
        }

        _stack = DevStack.Open();
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Directory:KeycloakBaseUrl"] = DevStack.AuthBase,
            ["Directory:Realm"] = "nightware",
            ["Directory:ClientId"] = "nightware-admin",
            ["Directory:ClientSecret"] = _stack.Env("NIGHTWARE_ADMIN_CLIENT_SECRET"),
            ["Directory:ServiceAccountScope"] = "kc-admin-api",
            ["StalwartManagement:ApiUrl"] = DevStack.MailBase + "/jmap/",
            ["StalwartManagement:Username"] = EnvOr("STALWART_ADMIN_USER", "admin"),
            ["StalwartManagement:Password"] = _stack.Env("STALWART_ADMIN_PASSWORD"),
            ["NextcloudProvisioning:BaseUrl"] = DevStack.CloudBase + "/",
            ["NextcloudProvisioning:Username"] = EnvOr("NEXTCLOUD_PROVISIONING_USER", "nightware-admin"),
            ["NextcloudProvisioning:Password"] = _stack.Env("NEXTCLOUD_PROVISIONING_PASSWORD"),
            ["NextcloudProvisioning:OidcProviderId"] = "keycloak",
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        services.AddKeycloakCore(configuration).AddKeycloakAdministration();
        services.AddStalwartAdministration(configuration);
        services.AddNextcloudAdministration(configuration);
        services.AddAdministrationUseCases();
        services.AddSingleton(new AdministrationOptions());
        services.AddSingleton<IAuditLog>(new NoAudit());
        services.AddSingleton<IAdminActor>(new GlobalActor());
        _stack.Trust(services);
        _services = services.BuildServiceProvider();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        _stack?.Dispose();
    }

    [Fact]
    public async Task A_group_s_list_Nextcloud_group_and_team_folder_follow_its_members()
    {
        Assert.SkipUnless(DevStack.Enabled, "Set NIGHTWARE_INTEGRATION=1 with the dev stack running.");
        using IServiceScope scope = _services!.CreateScope();
        IServiceProvider s = scope.ServiceProvider;
        Entity entity = (await s.GetRequiredService<IEntityRegistry>().ListAsync(Ct)).Value
            .First(e => e.Domains.Any(d => d.Name == DevStack.MailDomain));
        IdentityAccount alice = (await s.GetRequiredService<IIdentityAccounts>().FindByUsernameAsync("alice", Ct)).Value;
        string key = "it-" + Guid.NewGuid().ToString("N")[..8];
        MailAddress.TryParse($"{key}@{DevStack.MailDomain}", out MailAddress? address).ShouldBeTrue();
        string groupwareId = $"{entity.Alias}-{key}";
        Group? group = null;
        int? folderId = null;
        try
        {
            // Create: list (members only, one outside recipient), Nextcloud group and team folder.
            group = (await s.GetRequiredService<CreateGroup>().ExecuteAsync(entity.Id, new GroupForm(key)
            {
                Key = key,
                Mail = true,
                LocalPart = key,
                Domain = DevStack.MailDomain,
                ExternalRecipients = ["accountant@example.net"],
                Files = true,
                TeamFolder = true,
            }, Ct)).Value.Group;
            folderId = group.Files!.TeamFolderId;
            folderId.ShouldNotBeNull();

            // Keycloak kept the record, readable back.
            Group stored = (await s.GetRequiredService<IEntityGroups>().GetAsync(entity.Id, group.Id, Ct)).Value;
            stored.Mail!.ExternalRecipients.ShouldHaveSingleItem().Value.ShouldBe("accountant@example.net");
            stored.Files!.TeamFolderId.ShouldBe(folderId);

            // Join: alice reaches the list and the Nextcloud group.
            (await s.GetRequiredService<AddGroupMember>().ExecuteAsync(entity.Id, group.Id, alice.Id, Ct)).IsSuccess.ShouldBeTrue();
            MailingList list = (await s.GetRequiredService<IMailingLists>().FindByAddressAsync(address, Ct)).Value;
            list.Recipients.ShouldBe([alice.Email.ToLowerInvariant(), "accountant@example.net"], ignoreOrder: true);
            IGroupwareGroups groupware = s.GetRequiredService<IGroupwareGroups>();
            (await groupware.FindAsync(groupwareId, Ct)).Value.MemberUids.ShouldContain("alice");
            (await groupware.FindFolderAsync(folderId.Value, Ct)).Value.Groups.ShouldContain(groupwareId);

            GroupDetail detail = (await s.GetRequiredService<GetGroup>().ExecuteAsync(entity.Id, group.Id, Ct)).Value;
            detail.Links.ShouldAllBe(l => l.State == LinkState.Linked);

            // Leave: alice is out of both.
            (await s.GetRequiredService<RemoveGroupMember>().ExecuteAsync(entity.Id, group.Id, alice.Id, Ct)).IsSuccess.ShouldBeTrue();
            (await s.GetRequiredService<IMailingLists>().FindByAddressAsync(address, Ct)).Value.Recipients.ShouldBe(["accountant@example.net"]);
            (await groupware.FindAsync(groupwareId, Ct)).Value.MemberUids.ShouldNotContain("alice");

            // An address the list holds cannot be a second list's.
            (await s.GetRequiredService<CreateGroup>().ExecuteAsync(entity.Id, new GroupForm(key + "-b") { Mail = true, LocalPart = key, Domain = DevStack.MailDomain }, Ct))
                .Error!.Kind.ShouldBe(ErrorKind.Conflict);

            // Delete: list and Nextcloud group gone, the team folder kept with no one.
            (await s.GetRequiredService<DeleteGroup>().ExecuteAsync(entity.Id, group.Id, Ct)).IsSuccess.ShouldBeTrue();
            group = null;
            (await s.GetRequiredService<IMailingLists>().FindByAddressAsync(address, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
            (await groupware.FindAsync(groupwareId, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
            (await groupware.FindFolderAsync(folderId.Value, Ct)).Value.Groups.ShouldBeEmpty();
        }
        finally
        {
            if (group is not null)
            {
                await s.GetRequiredService<DeleteGroup>().ExecuteAsync(entity.Id, group.Id, Ct);
            }

            if (folderId is { } id)
            {
                await s.GetRequiredService<IGroupwareGroups>().DeleteFolderAsync(id, Ct);
            }
        }
    }

    private string EnvOr(string key, string fallback)
    {
        try
        {
            return _stack!.Env(key) is { Length: > 0 } value ? value : fallback;
        }
        catch (KeyNotFoundException)
        {
            return fallback;
        }
    }

    private sealed class GlobalActor : IAdminActor
    {
        public AdminActor Current { get; } = new("it-global", true, new HashSet<string>(), DateTimeOffset.UtcNow, "mfa");
    }

    private sealed class NoAudit : IAuditLog
    {
        public void Record(AuditEvent auditEvent)
        {
        }
    }
}

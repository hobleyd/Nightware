using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Nightware.Adapters.Dav.Administration;
using Nightware.Adapters.Directory;
using Nightware.Adapters.Directory.Administration;
using Nightware.Adapters.Jmap.Administration;
using Nightware.Application.Administration;
using Nightware.Application.Administration.SharedMailboxes;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;
using Nightware.Integration.Tests.Mail.Read;
using Nightware.Integration.Tests.Support;

namespace Nightware.Integration.Tests.Administration;

/// <summary>
/// Phase 10 against the running dev stack: the console's shared mailbox use cases over the real Keycloak and Stalwart
/// adapters, then NightMail's own calls for a shared mailbox through the in-process façade as alice (her Keycloak
/// token, exchanged for Stalwart). Opt-in (<c>NIGHTWARE_INTEGRATION=1</c>).
/// </summary>
public sealed class SharedMailboxesLiveTests : IAsyncDisposable
{
    private readonly DevStack? _stack;
    private readonly ServiceProvider? _services;

    public SharedMailboxesLiveTests()
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
            ["StalwartManagement:Username"] = "admin",
            ["StalwartManagement:Password"] = _stack.Env("STALWART_ADMIN_PASSWORD"),
            ["NextcloudProvisioning:BaseUrl"] = DevStack.CloudBase + "/",
            ["NextcloudProvisioning:Username"] = "nightware-admin",
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
    public async Task A_member_opens_the_shared_mailbox_through_the_facade_and_loses_it_when_removed()
    {
        Assert.SkipUnless(DevStack.Enabled, "Set NIGHTWARE_INTEGRATION=1 with the dev stack running.");
        using IServiceScope scope = _services!.CreateScope();
        IServiceProvider s = scope.ServiceProvider;
        Entity entity = (await s.GetRequiredService<IEntityRegistry>().ListAsync(Ct)).Value
            .First(e => e.Domains.Any(d => d.Name == DevStack.MailDomain));
        IdentityAccount alice = (await s.GetRequiredService<IIdentityAccounts>().FindByUsernameAsync("alice", Ct)).Value;
        string local = "it-" + Guid.NewGuid().ToString("N")[..8];
        MailAddress.TryParse($"{local}@{DevStack.MailDomain}", out MailAddress? address).ShouldBeTrue();
        ISharedMailAccounts server = s.GetRequiredService<ISharedMailAccounts>();
        SharedMailbox? mailbox = null;
        try
        {
            mailbox = (await s.GetRequiredService<CreateSharedMailbox>().ExecuteAsync(entity.Id, new SharedMailboxForm("IT desk") { LocalPart = local, Domain = DevStack.MailDomain }, Ct)).Value.Mailbox;
            (await s.GetRequiredService<IEntityMailboxes>().GetAsync(entity.Id, mailbox.Id, Ct)).Value.Address.ShouldBe(address);
            (await server.FindByAddressAsync(address, Ct)).Value.Name.ShouldBe("IT desk");

            // Join: the mail server lets alice's own mailbox open it.
            (await s.GetRequiredService<AddSharedMailboxMember>().ExecuteAsync(entity.Id, mailbox.Id, alice.Id, Ct)).IsSuccess.ShouldBeTrue();
            (await server.FindByAddressAsync(address, Ct)).Value.Members.ShouldHaveSingleItem().Address.ShouldBe(alice.Email, StringCompareShould.IgnoreCase);
            (await s.GetRequiredService<GetSharedMailbox>().ExecuteAsync(entity.Id, mailbox.Id, Ct)).Value.Links.ShouldAllBe(l => l.State == LinkState.Linked);

            // NightMail adds the shared mailbox: its profile, then the inbox probe, both with the raw '@' (endpoints.md).
            await using (MailLiveHost facade = MailLiveHost.Start())
            {
                using HttpClient graph = await facade.GraphAsync(Ct);
                using HttpResponseMessage profile = await graph.GetAsync($"/v1.0/users/{address}?$select=displayName,mail,userPrincipalName", Ct);
                profile.StatusCode.ShouldBe(HttpStatusCode.OK);
                JsonObject user = (await profile.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
                user["displayName"]!.GetValue<string>().ShouldBe("IT desk");
                user["mail"]!.GetValue<string>().ShouldBe(address.Value);

                using HttpResponseMessage inbox = await graph.GetAsync($"/v1.0/users/{address}/mailFolders/inbox?$select=id", Ct);
                inbox.StatusCode.ShouldBe(HttpStatusCode.OK);

                // She sends as it (the group account's own identity, her exchanged token), and it arrives from its address.
                string subject = "shared " + local;
                var message = new JsonObject
                {
                    ["message"] = new JsonObject
                    {
                        ["subject"] = subject,
                        ["body"] = new JsonObject { ["contentType"] = "text", ["content"] = "From the shared mailbox." },
                        ["toRecipients"] = new JsonArray(new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = alice.Email } }),
                    },
                    ["saveToSentItems"] = true,
                };
                using HttpResponseMessage sent = await graph.PostAsJsonAsync($"/v1.0/users/{address}/sendMail", message, Ct);
                sent.StatusCode.ShouldBe(HttpStatusCode.Accepted, await sent.Content.ReadAsStringAsync(Ct));

                JsonObject? arrived = null;
                for (int attempt = 0; attempt < 20 && arrived is null; attempt++)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
                    using HttpResponseMessage found = await graph.GetAsync($"/v1.0/me/mailFolders/inbox/messages?$select=subject,from&$top=25", Ct);
                    arrived = (await found.Content.ReadFromJsonAsync<JsonObject>(Ct))!["value"]!.AsArray()
                        .Select(static m => m!.AsObject()).FirstOrDefault(m => m["subject"]?.GetValue<string>() == subject);
                }

                arrived.ShouldNotBeNull("the message sent as the shared mailbox reached alice");

                // The rest of what NightMail may call for the account answers without a server error (no calendar or
                // contacts exist for it in Nextcloud: those are client errors it falls back from).
                foreach (string route in new[] { "mailboxSettings", "mailFolders?$top=50", "calendars", "contacts?$top=10", "todo/lists" })
                {
                    using HttpResponseMessage answer = await graph.GetAsync($"/v1.0/users/{address}/{route}", Ct);
                    ((int)answer.StatusCode).ShouldBeLessThan(500, $"{route}: {await answer.Content.ReadAsStringAsync(Ct)}");
                }
                arrived["from"]!["emailAddress"]!["address"]!.GetValue<string>().ShouldBe(address.Value, StringCompareShould.IgnoreCase);
            }

            // An address the shared mailbox holds cannot be a new user's.
            ProvisioningPlan plan = (await s.GetRequiredService<Nightware.Application.Administration.Users.PlanUserProvisioning>()
                .ExecuteAsync(new Nightware.Application.Administration.Users.NewUserRequest(entity.Id, address.Value, null, "It", "User", []), Ct)).Value;
            plan.For(BackendKind.Mail).Action.ShouldBe(PlannedAction.Conflict);

            // Leave: the mail server takes her access away at once.
            (await s.GetRequiredService<RemoveSharedMailboxMember>().ExecuteAsync(entity.Id, mailbox.Id, alice.Id, Ct)).IsSuccess.ShouldBeTrue();
            (await server.FindByAddressAsync(address, Ct)).Value.Members.ShouldBeEmpty();
            await using (MailLiveHost facade = MailLiveHost.Start())
            {
                using HttpClient graph = await facade.GraphAsync(Ct);
                using HttpResponseMessage inbox = await graph.GetAsync($"/v1.0/users/{address}/mailFolders/inbox?$select=id", Ct);
                inbox.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            }

            // Delete: with members (alice back in) the mailbox and its record go.
            (await s.GetRequiredService<AddSharedMailboxMember>().ExecuteAsync(entity.Id, mailbox.Id, alice.Id, Ct)).IsSuccess.ShouldBeTrue();
            (await s.GetRequiredService<DeleteSharedMailbox>().ExecuteAsync(entity.Id, mailbox.Id, address.Value, Ct)).IsSuccess.ShouldBeTrue();
            mailbox = null;
            (await server.FindByAddressAsync(address, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        }
        finally
        {
            if (mailbox is not null)
            {
                await s.GetRequiredService<DeleteSharedMailbox>().ExecuteAsync(entity.Id, mailbox.Id, address.Value, Ct);
            }
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

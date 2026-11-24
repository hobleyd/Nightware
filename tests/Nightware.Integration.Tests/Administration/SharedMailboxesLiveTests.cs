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

                // Its address book (phase 27): shared with the members like its calendar, so she adds a contact and sees it.
                using HttpResponseMessage contact = await graph.PostAsJsonAsync($"/v1.0/users/{address}/contacts",
                    new JsonObject { ["givenName"] = "Sam", ["surname"] = "Supplier", ["emailAddresses"] = new JsonArray(new JsonObject { ["address"] = "sam@supplier.example" }) }, Ct);
                contact.StatusCode.ShouldBe(HttpStatusCode.Created, await contact.Content.ReadAsStringAsync(Ct));
                JsonObject contacts = (await graph.GetFromJsonAsync<JsonObject>($"/v1.0/users/{address}/contacts?$select=id,displayName", Ct))!;
                contacts["value"]!.AsArray().Select(c => c!["displayName"]!.GetValue<string>()).ShouldContain("Sam Supplier");

                // Its files (phase 27): a folder the mailbox shares with its members, reached as its drive.
                JsonObject drive = (await graph.GetFromJsonAsync<JsonObject>($"/v1.0/users/{address}/drive", Ct))!;
                string driveId = drive["id"]!.GetValue<string>();
                using (var upload = new ByteArrayContent("rota\n"u8.ToArray()))
                {
                    upload.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
                    using HttpResponseMessage put = await graph.PutAsync($"/v1.0/users/{address}/drive/root:/rota.txt:/content", upload, Ct);
                    put.StatusCode.ShouldBe(HttpStatusCode.Created, await put.Content.ReadAsStringAsync(Ct));
                    JsonObject file = (await put.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
                    file["parentReference"]!["driveId"]!.GetValue<string>().ShouldBe(driveId);
                    JsonObject byDrive = (await graph.GetFromJsonAsync<JsonObject>($"/v1.0/drives/{Uri.EscapeDataString(driveId)}/items/{file["id"]!.GetValue<string>()}", Ct))!;
                    byDrive["name"]!.GetValue<string>().ShouldBe("rota.txt");
                }

                JsonObject children = (await graph.GetFromJsonAsync<JsonObject>($"/v1.0/users/{address}/drive/root/children?$select=name", Ct))!;
                children["value"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()).ShouldContain("rota.txt");

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

                // The rest of what NightMail may call for the account answers without a server error (no contacts exist
                // for it in Nextcloud: those are client errors it falls back from).
                foreach (string route in new[] { "mailboxSettings", "mailFolders?$top=50", "calendars", "contacts?$top=10", "todo/lists" })
                {
                    using HttpResponseMessage answer = await graph.GetAsync($"/v1.0/users/{address}/{route}", Ct);
                    ((int)answer.StatusCode).ShouldBeLessThan(500, $"{route}: {await answer.Content.ReadAsStringAsync(Ct)}");
                }
                arrived["from"]!["emailAddress"]!["address"]!.GetValue<string>().ShouldBe(address.Value, StringCompareShould.IgnoreCase);

                // Its calendar (ADR 0004 D6): shared with its members, empty at first, and events made in it are its own.
                using HttpResponseMessage calendars = await graph.GetAsync($"/v1.0/users/{address}/calendars", Ct);
                calendars.StatusCode.ShouldBe(HttpStatusCode.OK, await calendars.Content.ReadAsStringAsync(Ct));
                var calendar = (await calendars.Content.ReadFromJsonAsync<JsonObject>(Ct))!["value"]!.AsArray().ShouldHaveSingleItem()!;
                calendar["canEdit"]!.GetValue<bool>().ShouldBeTrue();
                calendar["owner"]!["address"]!.GetValue<string>().ShouldBe(address.Value, StringCompareShould.IgnoreCase);
                using HttpResponseMessage view = await graph.GetAsync($"/v1.0/users/{address}/calendarView?startDateTime=2020-01-01T00:00:00Z&endDateTime=2030-01-01T00:00:00Z", Ct);
                (await view.Content.ReadFromJsonAsync<JsonObject>(Ct))!["value"]!.AsArray().ShouldBeEmpty("Nextcloud's sample event is removed from a new account");
                using HttpResponseMessage booked = await graph.PostAsJsonAsync($"/v1.0/users/{address}/events", new JsonObject
                {
                    ["subject"] = "Desk rota " + local,
                    ["start"] = new JsonObject { ["dateTime"] = "2029-03-05T09:00:00", ["timeZone"] = "UTC" },
                    ["end"] = new JsonObject { ["dateTime"] = "2029-03-05T10:00:00", ["timeZone"] = "UTC" },
                }, Ct);
                booked.StatusCode.ShouldBe(HttpStatusCode.Created, await booked.Content.ReadAsStringAsync(Ct));
                using HttpResponseMessage rota = await graph.GetAsync($"/v1.0/users/{address}/calendarView?startDateTime=2029-03-01T00:00:00Z&endDateTime=2029-04-01T00:00:00Z", Ct);
                (await rota.Content.ReadFromJsonAsync<JsonObject>(Ct))!["value"]!.AsArray().ShouldHaveSingleItem()!["subject"]!.GetValue<string>().ShouldBe("Desk rota " + local);
            }

            (await s.GetRequiredService<GetSharedMailbox>().ExecuteAsync(entity.Id, mailbox.Id, Ct)).Value.Links
                .Single(l => l.Backend == BackendKind.Groupware).State.ShouldBe(LinkState.Linked);

            // An address the shared mailbox holds cannot be a new user's.
            // Rename: the identity members send as, and so the profile, takes the new name (phase 27).
            (await s.GetRequiredService<UpdateSharedMailbox>().ExecuteAsync(entity.Id, mailbox.Id, new SharedMailboxForm("Service desk"), Ct)).IsSuccess.ShouldBeTrue();
            (await server.FindByAddressAsync(address, Ct)).Value.Name.ShouldBe("Service desk");
            await using (MailLiveHost facade = MailLiveHost.Start())
            {
                using HttpClient graph = await facade.GraphAsync(Ct);
                JsonObject renamed = (await graph.GetFromJsonAsync<JsonObject>($"/v1.0/users/{address}?$select=displayName", Ct))!;
                renamed["displayName"]!.GetValue<string>().ShouldBe("Service desk");
            }

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
                using HttpResponseMessage calendar = await graph.GetAsync($"/v1.0/users/{address}/calendarView?startDateTime=2029-03-01T00:00:00Z&endDateTime=2029-04-01T00:00:00Z", Ct);
                ((int)calendar.StatusCode).ShouldBeOneOf([403, 404], "her calendar access went with her membership");
            }

            // Delete: with members (alice back in) the mailbox and its record go.
            (await s.GetRequiredService<AddSharedMailboxMember>().ExecuteAsync(entity.Id, mailbox.Id, alice.Id, Ct)).IsSuccess.ShouldBeTrue();
            (await s.GetRequiredService<DeleteSharedMailbox>().ExecuteAsync(entity.Id, mailbox.Id, address.Value, Ct)).IsSuccess.ShouldBeTrue();
            mailbox = null;
            (await server.FindByAddressAsync(address, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
            (await s.GetRequiredService<ISharedCalendars>().FindAsync(address, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound, "the calendar went with the mailbox");
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

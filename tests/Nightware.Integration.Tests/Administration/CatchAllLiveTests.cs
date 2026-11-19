using System.Net.Sockets;
using System.Text;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Nightware.Adapters.Dav.Administration;
using Nightware.Adapters.Directory;
using Nightware.Adapters.Directory.Administration;
using Nightware.Adapters.Jmap.Administration;
using Nightware.Application.Administration;
using Nightware.Application.Administration.Entities;
using Nightware.Domain.Administration;
using Nightware.Integration.Tests.Support;

namespace Nightware.Integration.Tests.Administration;

/// <summary>
/// A domain's catch-all against the running dev stack: the console's use case over the real Keycloak and Stalwart
/// adapters, then the mail server's answer at RCPT for an address no one has. Opt-in (<c>NIGHTWARE_INTEGRATION=1</c>).
/// </summary>
public sealed class CatchAllLiveTests : IAsyncDisposable
{
    private readonly DevStack? _stack;
    private readonly ServiceProvider? _services;

    public CatchAllLiveTests()
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
    public async Task Mail_to_an_unknown_address_is_accepted_for_the_chosen_user_and_refused_again_without_one()
    {
        Assert.SkipUnless(DevStack.Enabled, "Set NIGHTWARE_INTEGRATION=1 with the dev stack running.");
        using IServiceScope scope = _services!.CreateScope();
        IServiceProvider s = scope.ServiceProvider;
        Entity entity = (await s.GetRequiredService<IEntityRegistry>().ListAsync(Ct)).Value
            .First(e => e.Domains.Any(d => d.Name == DevStack.MailDomain));
        MailDomain domain = entity.Domains.First(d => d.Name == DevStack.MailDomain);
        IdentityAccount alice = (await s.GetRequiredService<IIdentityAccounts>().FindByUsernameAsync("bob", Ct)).Value;
        string? before = (await s.GetRequiredService<IEntityRegistry>().GetAsync(entity.Id, Ct)).Value.CatchAllFor(domain);
        SetDomainCatchAll set = s.GetRequiredService<SetDomainCatchAll>();
        string unknown = $"nobody-{Guid.NewGuid():N}@{DevStack.MailDomain}";
        try
        {
            (await set.ExecuteAsync(entity.Id, domain.Name, alice.Id, Ct)).Value!.Value.ShouldBe(alice.Email, StringCompareShould.IgnoreCase);
            (await s.GetRequiredService<IEntityRegistry>().GetAsync(entity.Id, Ct)).Value.CatchAllFor(domain).ShouldBe(alice.Id);
            (await s.GetRequiredService<IMailDomains>().FindAsync(domain, Ct)).Value.CatchAllAddress.ShouldBe(alice.Email, StringCompareShould.IgnoreCase);
            (await RcptAsync(unknown)).ShouldStartWith("250");

            (await set.ExecuteAsync(entity.Id, domain.Name, null, Ct)).Value.ShouldBeNull();
            (await s.GetRequiredService<IMailDomains>().FindAsync(domain, Ct)).Value.CatchAllAddress.ShouldBeNull();
            (await RcptAsync(unknown)).ShouldStartWith("550");
        }
        finally
        {
            await set.ExecuteAsync(entity.Id, domain.Name, before, CancellationToken.None);
        }
    }

    /// <summary>The mail server's reply to <c>RCPT TO</c> for <paramref name="recipient"/> from an outside sender, over its published SMTP port.</summary>
    private async Task<string> RcptAsync(string recipient)
    {
        // deploy/docker-compose.yml publishes Stalwart's port 25 as ${SMTP_PORT:-2525}.
        int port = _stack!.Has("SMTP_PORT") && int.TryParse(_stack.Env("SMTP_PORT"), out int p) ? p : 2525;
        string host = Environment.GetEnvironmentVariable("NIGHTWARE_CONNECT_ADDRESS") is { Length: > 0 } a ? a : "127.0.0.1";
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, Ct);
        await using NetworkStream stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };

        async Task<string> ReplyAsync()
        {
            string line;
            do
            {
                line = await reader.ReadLineAsync(Ct) ?? throw new IOException("The mail server closed the connection.");
            }
            while (line.Length > 3 && line[3] == '-');
            return line;
        }

        (await ReplyAsync()).ShouldStartWith("220");
        await writer.WriteLineAsync("EHLO probe.example.net");
        (await ReplyAsync()).ShouldStartWith("250");
        await writer.WriteLineAsync("MAIL FROM:<someone@example.net>");
        (await ReplyAsync()).ShouldStartWith("250");
        await writer.WriteLineAsync($"RCPT TO:<{recipient}>");
        string reply = await ReplyAsync();
        await writer.WriteLineAsync("QUIT");
        return reply;
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

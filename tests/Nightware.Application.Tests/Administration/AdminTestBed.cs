using Microsoft.Extensions.Time.Testing;

using Nightware.Application.Administration;
using Nightware.Application.Administration.Entities;
using Nightware.Application.Administration.Users;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Administration;

/// <summary>
/// Substituted ports with a small world: entity <c>acme</c> (acme.example), entity <c>other</c>
/// (other.example), and helpers to place users in it. Unarranged lookups answer NotFound.
/// </summary>
internal sealed class AdminTestBed
{
    public const string Acme = "e-acme";
    public const string Other = "e-other";

    private readonly MutableActor _actor = new();

    public AdminTestBed()
    {
        Entities.GetAsync(Acme, Arg.Any<CancellationToken>()).Returns(AcmeEntity);
        Entities.GetAsync(Other, Arg.Any<CancellationToken>()).Returns(OtherEntity);
        Entities.GetMembershipsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<string>>.Success([]));
        Entities.AddMemberAsync(default!, default!, default).ReturnsForAnyArgs(Result.Success());
        Entities.GrantAdminAsync(default!, default!, default).ReturnsForAnyArgs(Result.Success());
        Entities.RevokeAdminAsync(default!, default!, default).ReturnsForAnyArgs(Result.Success());
        Scopes.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new AdminScope(false, new HashSet<string>()));

        Identities.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Error.NotFound("no user"));
        Identities.FindByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Error.NotFound("no user"));
        Identities.FindByUsernameAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Error.NotFound("no user"));
        Identities.CreateAsync(default!, default).ReturnsForAnyArgs(c =>
        {
            // Null while a test re-arranges the call with default arguments.
            NewIdentity? n = c.Arg<NewIdentity?>();
            return n is null ? default : Result<IdentityAccount>.Success(new IdentityAccount("u-new", n.Username, n.Email.Value, true) { FirstName = n.FirstName, LastName = n.LastName });
        });
        Identities.SetNameAsync(default!, default, default, default).ReturnsForAnyArgs(Result.Success());
        Identities.SetAliasesAsync(default!, default!, default).ReturnsForAnyArgs(Result.Success());
        Identities.SetEnabledAsync(default!, default, default).ReturnsForAnyArgs(Result.Success());
        Identities.EndSessionsAsync(default!, default).ReturnsForAnyArgs(Result.Success());
        Identities.SendActionEmailAsync(default!, default!, default, default).ReturnsForAnyArgs(Result.Success());
        Identities.SetTemporaryPasswordAsync(default!, default!, default).ReturnsForAnyArgs(Result.Success());
        Identities.AddRequiredActionAsync(default!, default, default).ReturnsForAnyArgs(Result.Success());
        Identities.SetGlobalAdminAsync(default!, default, default).ReturnsForAnyArgs(Result.Success());
        Identities.SetDeletionMarkerAsync(default!, default, default, default).ReturnsForAnyArgs(Result.Success());

        Mail.FindByAddressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Error.NotFound("no mailbox"));
        Mail.CreateAsync(default!, default!, default).ReturnsForAnyArgs(c => c.Arg<MailAddress?>() is { } a ? Result<MailAccount>.Success(new MailAccount("m-new", a.Value, true)) : default);
        Mail.SetAliasesAsync(default!, default!, default).ReturnsForAnyArgs(Result.Success());
        Mail.GetUsageAsync(default!, default).ReturnsForAnyArgs(Result<long>.Success(1234));
        Mail.DestroyAsync(default!, default).ReturnsForAnyArgs(Result.Success());

        MailDomains.FindAsync(default!, default).ReturnsForAnyArgs(Error.NotFound("no domain"));
        MailDomains.CreateAsync(default!, default!, default).ReturnsForAnyArgs(c => c.Arg<MailDomain?>() is { } d ? Result<MailDomainInfo>.Success(new MailDomainInfo("d-new", d, true)) : default);

        Groupware.FindAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Error.NotFound("no user"));
        Groupware.PreProvisionAsync(default!, default!, default!, default).ReturnsForAnyArgs(Result.Success());
        Groupware.SetEnabledAsync(default!, default, default).ReturnsForAnyArgs(Result.Success());
        Groupware.DeleteAsync(default!, default).ReturnsForAnyArgs(Result.Success());

        Passwords.Generate().Returns(new TemporaryCredential("Generated-Pass-1"));
        ActAsGlobal();
    }

    public static Entity AcmeEntity { get; } = new(Acme, "acme", "Acme", [Domain("acme.example")]);

    public static Entity OtherEntity { get; } = new(Other, "other", "Other", [Domain("other.example")]);

    public IEntityRegistry Entities { get; } = Substitute.For<IEntityRegistry>();

    public IIdentityAccounts Identities { get; } = Substitute.For<IIdentityAccounts>();

    public IAdminScopeSource Scopes { get; } = Substitute.For<IAdminScopeSource>();

    public IMailAccounts Mail { get; } = Substitute.For<IMailAccounts>();

    public IMailDomains MailDomains { get; } = Substitute.For<IMailDomains>();

    public IGroupwareAccounts Groupware { get; } = Substitute.For<IGroupwareAccounts>();

    public IPasswordGenerator Passwords { get; } = Substitute.For<IPasswordGenerator>();

    public RecordingAuditLog Audit { get; } = new();

    public AdministrationOptions Options { get; } = new();

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 17, 9, 0, 0, TimeSpan.Zero));

    public IAdminActor Actor => _actor;

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static MailDomain Domain(string name) => MailDomain.TryParse(name, out MailDomain? d) ? d : throw new ArgumentException(name);

    public static MailAddress Address(string value) => MailAddress.TryParse(value, out MailAddress? a) ? a : throw new ArgumentException(value);

    public AdminActor ActAsGlobal(string userId = "u-admin") => _actor.Current = new AdminActor(userId, true, new HashSet<string>(), Time.GetUtcNow(), "mfa");

    public AdminActor ActAsEntityAdmin(string entityId = Acme, string userId = "u-eadmin") =>
        _actor.Current = new AdminActor(userId, false, new HashSet<string> { entityId }, Time.GetUtcNow(), "mfa");

    public AdminActor ActAsNobody() => _actor.Current = new AdminActor("u-nobody", false, new HashSet<string>(), Time.GetUtcNow(), null);

    public void SignedInAgo(TimeSpan age) => _actor.Current = _actor.Current with { AuthenticatedAt = Time.GetUtcNow() - age };

    /// <summary>A Keycloak user that exists, in <paramref name="entities"/>, with an optional admin scope.</summary>
    public IdentityAccount AddUser(string id, string username, string email, bool enabled = true, string[]? entities = null, AdminScope? scope = null)
    {
        var user = new IdentityAccount(id, username, email, enabled);
        Identities.GetAsync(id, Arg.Any<CancellationToken>()).Returns(user);
        Identities.FindByEmailAsync(email, Arg.Any<CancellationToken>()).Returns(user);
        Identities.FindByUsernameAsync(username, Arg.Any<CancellationToken>()).Returns(user);
        Entities.GetMembershipsAsync(id, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<string>>.Success(entities ?? [Acme]));
        Scopes.ResolveAsync(id, Arg.Any<CancellationToken>()).Returns(scope ?? new AdminScope(false, new HashSet<string>()));
        return user;
    }

    public void Update(IdentityAccount user) => Identities.GetAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);

    public MailAccount AddMailbox(string address, string id = "m-1", bool individual = true)
    {
        var account = new MailAccount(id, address, individual);
        Mail.FindByAddressAsync(address, Arg.Any<CancellationToken>()).Returns(account);
        return account;
    }

    public GroupwareAccount AddCloudUser(string uid, string? email, bool enabled = true, bool oidc = true)
    {
        var account = new GroupwareAccount(uid, email, uid, enabled, oidc);
        Groupware.FindAsync(uid, Arg.Any<CancellationToken>()).Returns(account);
        return account;
    }

    public UserAccess UserAccess() => new(Actor, Identities, Entities, Scopes, Mail, Groupware);

    public ProvisioningPlanner Planner() => new(Actor, Entities, Identities, Scopes, Mail, Groupware);

    public CredentialIssuer Credentials() => new(Identities, Passwords, Options);

    public DomainProvisioning DomainProvisioning() => new(MailDomains);

    public DeletionGuard DeletionGuard() => new(Actor, UserAccess(), Options, Time);

    private sealed class MutableActor : IAdminActor
    {
        public AdminActor Current { get; set; } = null!;
    }
}

internal sealed class RecordingAuditLog : IAuditLog
{
    public List<AuditEvent> Events { get; } = [];

    public AuditEvent Single(string action) => Events.Single(e => e.Action == action);

    public void Record(AuditEvent auditEvent) => Events.Add(auditEvent);
}

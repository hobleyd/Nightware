using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Administration;

public sealed class AdminAuthorizationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    private static AdminActor Global => new("g", true, new HashSet<string>(), Now, "mfa");

    private static AdminActor EntityAdmin(params string[] entities) => new("a", false, new HashSet<string>(entities), Now, "mfa");

    private static AdminScope NoScope => new(false, new HashSet<string>());

    [Fact]
    public void Admin_and_global_checks()
    {
        AdminAuthorization.RequireAdmin(Global).IsSuccess.ShouldBeTrue();
        AdminAuthorization.RequireAdmin(EntityAdmin("e1")).IsSuccess.ShouldBeTrue();
        AdminAuthorization.RequireAdmin(EntityAdmin()).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        AdminAuthorization.RequireGlobal(Global).IsSuccess.ShouldBeTrue();
        AdminAuthorization.RequireGlobal(EntityAdmin("e1")).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public void Entity_check_allows_own_entity_only()
    {
        AdminAuthorization.RequireEntity(EntityAdmin("e1"), "e1").IsSuccess.ShouldBeTrue();
        AdminAuthorization.RequireEntity(EntityAdmin("e1"), "e2").Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        AdminAuthorization.RequireEntity(EntityAdmin("e1"), null).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        AdminAuthorization.RequireEntity(Global, "e2").IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_global_admin_may_manage_any_user() =>
        AdminAuthorization.RequireMayManageUser(Global, [], new AdminScope(true, new HashSet<string>())).IsSuccess.ShouldBeTrue();

    [Fact]
    public void An_entity_admin_may_manage_a_plain_member_of_their_entity() =>
        AdminAuthorization.RequireMayManageUser(EntityAdmin("e1"), ["e1"], NoScope).IsSuccess.ShouldBeTrue();

    [Fact]
    public void An_entity_admin_may_manage_a_fellow_admin_of_the_same_entity() =>
        AdminAuthorization.RequireMayManageUser(EntityAdmin("e1"), ["e1"], new AdminScope(false, new HashSet<string> { "e1" })).IsSuccess.ShouldBeTrue();

    [Theory]
    [InlineData("")]
    [InlineData("e2")]
    [InlineData("e1,e2")]
    public void An_entity_admin_may_not_manage_users_outside_or_partly_outside_their_scope(string targetEntities) =>
        AdminAuthorization.RequireMayManageUser(EntityAdmin("e1"), targetEntities.Split(',', StringSplitOptions.RemoveEmptyEntries), NoScope).Error!.Kind.ShouldBe(ErrorKind.Forbidden);

    [Fact]
    public void An_entity_admin_may_not_manage_a_global_admin_in_their_entity() =>
        AdminAuthorization.RequireMayManageUser(EntityAdmin("e1"), ["e1"], new AdminScope(true, new HashSet<string>())).Error!.Kind.ShouldBe(ErrorKind.Forbidden);

    [Fact]
    public void An_entity_admin_may_not_manage_an_admin_of_another_entity() =>
        AdminAuthorization.RequireMayManageUser(EntityAdmin("e1"), ["e1"], new AdminScope(false, new HashSet<string> { "e1", "e2" })).Error!.Kind.ShouldBe(ErrorKind.Forbidden);

    [Fact]
    public void Addresses_must_be_on_the_entitys_domains()
    {
        Entity acme = AdminTestBed.AcmeEntity;
        AdminAuthorization.RequireAddressInEntity(acme, AdminTestBed.Address("c@acme.example")).IsSuccess.ShouldBeTrue();
        AdminAuthorization.RequireAddressInEntity(acme, AdminTestBed.Address("c@other.example")).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public void Not_self()
    {
        AdminAuthorization.RequireNotSelf(Global, "g").Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        AdminAuthorization.RequireNotSelf(Global, "other").IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void Data_deletion_needs_a_disabled_user()
    {
        AdminAuthorization.RequireDisabled(new IdentityAccount("u", "u", "u@acme.example", Enabled: false)).IsSuccess.ShouldBeTrue();
        AdminAuthorization.RequireDisabled(new IdentityAccount("u", "u", "u@acme.example", Enabled: true)).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public void Data_deletion_is_global_unless_the_deployment_opts_entity_admins_in()
    {
        var options = new AdministrationOptions();
        AdminAuthorization.RequireMayDeleteData(Global, "e1", options).IsSuccess.ShouldBeTrue();
        AdminAuthorization.RequireMayDeleteData(EntityAdmin("e1"), "e1", options).Error!.Kind.ShouldBe(ErrorKind.Forbidden);

        options.EntityAdminsMayDeleteData = true;
        AdminAuthorization.RequireMayDeleteData(EntityAdmin("e1"), "e1", options).IsSuccess.ShouldBeTrue();
        AdminAuthorization.RequireMayDeleteData(EntityAdmin("e1"), "e2", options).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public void Fresh_authentication_needs_a_recent_sign_in()
    {
        var options = new AdministrationOptions { DestructiveActionMaxAge = TimeSpan.FromMinutes(5) };
        AdminAuthorization.RequireFreshAuthentication(Global with { AuthenticatedAt = Now.AddMinutes(-5) }, Now, options).IsSuccess.ShouldBeTrue();

        Error stale = AdminAuthorization.RequireFreshAuthentication(Global with { AuthenticatedAt = Now.AddMinutes(-6) }, Now, options).Error!;
        stale.Kind.ShouldBe(ErrorKind.Unauthorized);
        stale.Code.ShouldBe("reauthenticationRequired");
    }

    [Fact]
    public void Fresh_authentication_needs_an_accepted_acr_when_any_are_configured()
    {
        var options = new AdministrationOptions();
        AdminAuthorization.RequireFreshAuthentication(Global with { Acr = null }, Now, options).IsSuccess.ShouldBeTrue();

        options.AcceptedAcrValues.Add("mfa");
        AdminAuthorization.RequireFreshAuthentication(Global, Now, options).IsSuccess.ShouldBeTrue();
        AdminAuthorization.RequireFreshAuthentication(Global with { Acr = "pwd" }, Now, options).Error!.Code.ShouldBe("reauthenticationRequired");
        AdminAuthorization.RequireFreshAuthentication(Global with { Acr = null }, Now, options).Error!.Code.ShouldBe("reauthenticationRequired");
    }

    [Theory]
    [InlineData("carol@acme.example", true)]
    [InlineData(" Carol@ACME.example ", true)]
    [InlineData("carol@acme.exampl", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Typed_confirmation_must_match_the_address(string? typed, bool ok) =>
        AdminAuthorization.RequireTypedConfirmation(typed, "carol@acme.example").IsSuccess.ShouldBe(ok);

    [Fact]
    public void Nobody_manages_a_keycloak_administrator_through_the_console() =>
        AdminAuthorization.RequireMayManageUser(Global, ["e1"], new AdminScope(false, new HashSet<string>(), AdministersDirectory: true)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
}

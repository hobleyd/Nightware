using Nightware.Domain.Administration;

namespace Nightware.Domain.Tests.Administration;

public sealed class AdministrationModelTests
{
    private static MailDomain Domain(string name) => MailDomain.TryParse(name, out MailDomain? d) ? d : throw new ArgumentException(name);

    private static MailAddress Address(string value) => MailAddress.TryParse(value, out MailAddress? a) ? a : throw new ArgumentException(value);

    [Fact]
    public void An_entity_owns_addresses_on_its_domains_only()
    {
        var acme = new Entity("e1", "acme", "Acme", [Domain("acme.example"), Domain("acme.org")]);

        acme.Owns(Address("carol@acme.example")).ShouldBeTrue();
        acme.Owns(Address("carol@ACME.org")).ShouldBeTrue();
        acme.Owns(Address("carol@sub.acme.example")).ShouldBeFalse();
        acme.Owns(Address("carol@other.example")).ShouldBeFalse();
    }

    [Theory]
    [InlineData("Carol", "Jones", "Carol Jones")]
    [InlineData("Carol", null, "Carol")]
    [InlineData(null, " ", "carol")]
    public void The_display_name_falls_back_to_the_username(string? first, string? last, string expected) =>
        new IdentityAccount("u1", "carol", "carol@acme.example", true) { FirstName = first, LastName = last }.DisplayName.ShouldBe(expected);

    [Fact]
    public void A_temporary_credential_never_prints_its_password()
    {
        var credential = new TemporaryCredential("s3cret-Value");

        credential.ToString().ShouldNotContain("s3cret");
        $"{credential}".ShouldNotContain("s3cret");
        credential.Password.ShouldBe("s3cret-Value");
    }

    [Fact]
    public void An_actor_manages_their_own_entities_or_all_when_global()
    {
        var entityAdmin = new AdminActor("a", false, new HashSet<string> { "e1" }, DateTimeOffset.UnixEpoch, null);
        var global = entityAdmin with { IsGlobal = true, EntityIds = new HashSet<string>() };
        var nobody = entityAdmin with { EntityIds = new HashSet<string>() };

        entityAdmin.MayManage("e1").ShouldBeTrue();
        entityAdmin.MayManage("e2").ShouldBeFalse();
        entityAdmin.MayManage(null).ShouldBeFalse();
        entityAdmin.IsAdmin.ShouldBeTrue();
        global.MayManage("e2").ShouldBeTrue();
        global.MayManage(null).ShouldBeTrue();
        nobody.IsAdmin.ShouldBeFalse();
    }

    [Fact]
    public void A_plan_with_any_conflict_is_blocked()
    {
        var plan = new ProvisioningPlan(
        [
            new PlannedStep(BackendKind.Identity, PlannedAction.Create, "c"),
            new PlannedStep(BackendKind.Mail, PlannedAction.Conflict, "x"),
            new PlannedStep(BackendKind.Groupware, PlannedAction.Link, "l"),
        ]);

        plan.HasConflict.ShouldBeTrue();
        plan.For(BackendKind.Groupware).Action.ShouldBe(PlannedAction.Link);
        (plan with { Steps = [plan.Steps[0]] }).HasConflict.ShouldBeFalse();
    }
}

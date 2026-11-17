using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Administration;

public sealed class LinkStateEvaluatorTests
{
    private const string Address = "carol@acme.example";

    private static readonly Result<IdentityAccount> Absent = Error.NotFound("none");

    [Fact]
    public void Identity_is_linked_or_disabled()
    {
        LinkStateEvaluator.Identity(new IdentityAccount("u", "carol", Address, true)).State.ShouldBe(LinkState.Linked);
        BackendLink disabled = LinkStateEvaluator.Identity(new IdentityAccount("u", "carol", Address, false));
        disabled.State.ShouldBe(LinkState.Disabled);
        disabled.DownstreamId.ShouldBe("u");
    }

    [Fact]
    public void Mail_states()
    {
        LinkStateEvaluator.Mail(Address, Error.NotFound("x")).State.ShouldBe(LinkState.Missing);
        LinkStateEvaluator.Mail(Address, Error.BackendUnavailable("down")).State.ShouldBe(LinkState.Error);
        LinkStateEvaluator.Mail(Address, new MailAccount("m", "Carol@acme.example", true)).ShouldBe(new BackendLink(BackendKind.Mail, LinkState.Linked, "Mailbox Carol@acme.example", "m"));
        LinkStateEvaluator.Mail(Address, new MailAccount("m", Address, false)).State.ShouldBe(LinkState.Conflict);
        LinkStateEvaluator.Mail(Address, new MailAccount("m", "c.jones@acme.example", true)).State.ShouldBe(LinkState.Conflict);
    }

    [Fact]
    public void Groupware_states()
    {
        LinkStateEvaluator.Groupware("carol", Address, Error.NotFound("x")).State.ShouldBe(LinkState.Missing);
        LinkStateEvaluator.Groupware("carol", Address, Error.BackendUnavailable("x")).State.ShouldBe(LinkState.Error);
        LinkStateEvaluator.Groupware("carol", Address, new GroupwareAccount("carol", Address, "C", true, true)).State.ShouldBe(LinkState.Linked);
        LinkStateEvaluator.Groupware("carol", Address, new GroupwareAccount("carol", null, "C", true, true)).State.ShouldBe(LinkState.Linked);
        LinkStateEvaluator.Groupware("carol", Address, new GroupwareAccount("carol", Address, "C", false, true)).State.ShouldBe(LinkState.Disabled);
        LinkStateEvaluator.Groupware("carol", Address, new GroupwareAccount("carol", "x@y.example", "C", true, true)).State.ShouldBe(LinkState.Conflict);
        LinkStateEvaluator.Groupware("carol", Address, new GroupwareAccount("carol", Address, "C", true, false)).State.ShouldBe(LinkState.Conflict);
    }

    [Fact]
    public void A_new_person_is_created()
    {
        (PlannedStep step, IdentityAccount? existing) = LinkStateEvaluator.IdentityStep("e1", null, "carol", Absent, Absent, []);
        step.Action.ShouldBe(PlannedAction.Create);
        existing.ShouldBeNull();
    }

    [Fact]
    public void An_existing_address_without_an_entity_is_linked_and_joins()
    {
        var carol = new IdentityAccount("u", "carol", Address, true);
        (PlannedStep step, IdentityAccount? existing) = LinkStateEvaluator.IdentityStep("e1", null, "carol", carol, carol, []);
        step.Action.ShouldBe(PlannedAction.Link);
        step.Reason.ShouldContain("added to the entity");
        existing.ShouldBe(carol);
    }

    [Fact]
    public void An_existing_member_is_linked() =>
        LinkStateEvaluator.IdentityStep("e1", "carol", "carol", new IdentityAccount("u", "carol", Address, true), Absent, ["e1"]).Step.Action.ShouldBe(PlannedAction.Link);

    [Fact]
    public void An_address_owned_by_a_different_username_is_a_conflict() =>
        LinkStateEvaluator.IdentityStep("e1", "cjones", "cjones", new IdentityAccount("u", "carol", Address, true), Absent, []).Step.Action.ShouldBe(PlannedAction.Conflict);

    [Fact]
    public void A_user_of_another_entity_is_a_conflict() =>
        LinkStateEvaluator.IdentityStep("e1", null, "carol", new IdentityAccount("u", "carol", Address, true), Absent, ["e2"]).Step.Action.ShouldBe(PlannedAction.Conflict);

    [Fact]
    public void A_taken_username_is_a_conflict() =>
        LinkStateEvaluator.IdentityStep("e1", null, "carol", Absent, new IdentityAccount("u", "carol", "carol@other.example", true), []).Step.Action.ShouldBe(PlannedAction.Conflict);

    [Theory]
    [InlineData(LinkState.Missing, PlannedAction.Create)]
    [InlineData(LinkState.Linked, PlannedAction.Link)]
    [InlineData(LinkState.Disabled, PlannedAction.Update)]
    [InlineData(LinkState.Pending, PlannedAction.Skip)]
    [InlineData(LinkState.Conflict, PlannedAction.Conflict)]
    [InlineData(LinkState.Error, PlannedAction.Conflict)]
    public void Backend_states_become_plan_steps(LinkState state, PlannedAction action) =>
        LinkStateEvaluator.Step(new BackendLink(BackendKind.Mail, state, "d"), "create").Action.ShouldBe(action);

    [Fact]
    public void A_privileged_mailbox_is_never_linked() =>
        LinkStateEvaluator.Mail(Address, new MailAccount("m", Address, true) { IsPrivileged = true }).State.ShouldBe(LinkState.Conflict);

    [Fact]
    public void A_mailbox_with_sign_in_blocked_is_disabled_not_missing() =>
        LinkStateEvaluator.Mail(Address, new MailAccount("m", Address, true) { Enabled = false }).State.ShouldBe(LinkState.Disabled);
}

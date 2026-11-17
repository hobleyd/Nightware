using Nightware.Application.Administration;
using Nightware.Application.Administration.Entities;
using Nightware.Application.Administration.Status;
using Nightware.Application.Administration.Users;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Administration;

public sealed class AttentionTests
{
    private readonly AdminTestBed _bed = new();

    private static CancellationToken Ct => AdminTestBed.Ct;

    private GetAttention Attention => new(
        new ListEntities(_bed.Actor, _bed.Entities),
        new GetEntity(_bed.Actor, _bed.Entities, _bed.DomainProvisioning()),
        new ListUsers(_bed.Actor, _bed.Entities, _bed.UserAccess()));

    public AttentionTests()
    {
        _bed.Entities.ListAsync(Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<Entity>>.Success([AdminTestBed.AcmeEntity]));
        _bed.MailDomains.FindAsync(default!, Ct).ReturnsForAnyArgs(c => Result<MailDomainInfo>.Success(new MailDomainInfo("d", c.Arg<MailDomain>(), true)));
    }

    [Fact]
    public async Task Lists_incomplete_offboardings_first_then_repairs_and_skips_users_in_order()
    {
        _bed.Entities.ListMembersAsync(AdminTestBed.Acme, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<IdentityAccount>>.Success(
        [
            new IdentityAccount("u1", "amy", "amy@acme.example", true),
            new IdentityAccount("u2", "bob", "bob@acme.example", false),
            new IdentityAccount("u3", "cat", "cat@acme.example", true),
        ]));
        _bed.AddMailbox("bob@acme.example", "m-bob");
        _bed.AddMailbox("cat@acme.example", "m-cat");
        _bed.AddCloudUser("cat", "cat@acme.example");

        Attention found = (await Attention.ExecuteAsync(Ct)).Value;

        found.Users.Select(u => (u.User.User.Identity.Username, u.Status)).ShouldBe(
            [("bob", UserStatus.OffboardingIncomplete), ("amy", UserStatus.NeedsRepair)]);
        found.Domains.ShouldBeEmpty();
        found.IsClear.ShouldBeFalse();
    }

    [Fact]
    public async Task A_domain_the_mail_server_does_not_serve_needs_attention()
    {
        _bed.MailDomains.FindAsync(default!, Ct).ReturnsForAnyArgs(Error.NotFound("no domain"));
        _bed.Entities.ListMembersAsync(AdminTestBed.Acme, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<IdentityAccount>>.Success([]));

        Attention found = (await Attention.ExecuteAsync(Ct)).Value;

        found.Domains.Single().Domain.Domain.Name.ShouldBe("acme.example");
    }

    [Fact]
    public async Task An_entity_that_cannot_be_read_is_named_never_silently_clear()
    {
        _bed.Entities.ListMembersAsync(AdminTestBed.Acme, Arg.Any<CancellationToken>()).Returns(Error.BackendUnavailable("Keycloak is down."));

        Attention found = (await Attention.ExecuteAsync(Ct)).Value;

        found.NotChecked.Single().Id.ShouldBe(AdminTestBed.Acme);
        found.IsClear.ShouldBeFalse();
    }
}

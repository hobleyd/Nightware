using Microsoft.Extensions.Logging.Abstractions;

using Nightware.Adapters.Directory.Core.Admin;
using Nightware.Adapters.Directory.MailboxSettings;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Directory.Tests.MailboxSettings;

public sealed class KeycloakInternalDomainsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IKeycloakAdminClient _keycloak = Substitute.For<IKeycloakAdminClient>();

    private KeycloakInternalDomains Domains() => new(_keycloak, NullLogger<KeycloakInternalDomains>.Instance);

    private static KeycloakOrganization Entity(string name, params string[] domains) =>
        new() { Name = name, Domains = [.. domains.Select(static d => new KeycloakOrganizationDomain { Name = d })] };

    [Fact]
    public async Task The_entity_owning_the_mailbox_domain_gives_all_its_domains()
    {
        _keycloak.SearchOrganizationsAsync("acme.example", true, Ct).Returns(new List<KeycloakOrganization> { Entity("Acme", "Acme.Example", "acme.test") });

        (await Domains().ForAsync(new MailboxRef("jo@Acme.Example"), Ct)).ShouldBe(["acme.example", "acme.test"]);
    }

    [Fact]
    public async Task A_name_match_that_does_not_own_the_domain_is_ignored()
    {
        _keycloak.SearchOrganizationsAsync("acme.example", true, Ct).Returns(new List<KeycloakOrganization> { Entity("acme.example", "other.test") });

        (await Domains().ForAsync(new MailboxRef("jo@acme.example"), Ct)).ShouldBe(["acme.example"]);
    }

    [Fact]
    public async Task Without_keycloak_only_the_mailbox_domain_is_internal()
    {
        _keycloak.SearchOrganizationsAsync("acme.example", true, Ct).Returns(Error.BackendUnavailable("down"));

        (await Domains().ForAsync(new MailboxRef("jo@acme.example"), Ct)).ShouldBe(["acme.example"]);
    }
}

using System.Text.Json;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Core.Session;
using Nightware.Adapters.Jmap.Tests.Support;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Tests.Core.Session;

public sealed class JmapAccountResolverTests
{
    private static readonly JmapSession Alice = JmapSession.Create(
        JsonSerializer.Deserialize(Fixtures.AliceSession, JmapJsonContext.Default.JmapSessionDocument)!,
        new Uri("http://stalwart:8080/jmap/session"),
        useSessionOrigin: true,
        500,
        16);

    private static Result<JmapAccount> Resolve(string address, string? caller = null, string capability = JmapCapabilities.Mail) =>
        JmapAccountResolver.Resolve(Alice, new MailboxRef(address), caller, capability);

    [Theory]
    [InlineData("alice@nightware.localhost")]
    [InlineData("Alice@Nightware.Localhost")]
    [InlineData(" alice@nightware.localhost ")]
    public void Own_address_resolves_to_the_primary_account(string address)
    {
        var account = Resolve(address).Value;

        account.AccountId.ShouldBe("c");
        account.IsPrimary.ShouldBeTrue();
        account.IsReadOnly.ShouldBeFalse();
        account.Session.ShouldBeSameAs(Alice);
    }

    [Fact]
    public void The_principal_mail_counts_as_own_address()
    {
        // An alias the session username does not show (e.g. Keycloak email differs from the Stalwart login).
        Resolve("a.smith@nightware.localhost", caller: "a.smith@nightware.localhost").Value.AccountId.ShouldBe("c");
    }

    [Fact]
    public void Shared_mailboxes_resolve_by_account_name()
    {
        var bob = Resolve("BOB@nightware.localhost").Value;

        bob.AccountId.ShouldBe("d");
        bob.IsPrimary.ShouldBeFalse();
        bob.Name.ShouldBe("bob@nightware.localhost");
    }

    [Fact]
    public void Bare_account_names_are_qualified_with_the_username_domain()
    {
        var reception = Resolve("reception@nightware.localhost").Value;

        reception.AccountId.ShouldBe("f");
        reception.IsReadOnly.ShouldBeTrue();
        Resolve("reception@other.example").Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Theory]
    [InlineData("carol@nightware.localhost")]
    [InlineData("")]
    [InlineData("reception")]
    public void Unknown_mailboxes_are_not_found(string address)
    {
        Resolve(address).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public void A_shared_account_without_the_capability_is_forbidden()
    {
        // bob's account is shared for mail only — no submission/vacation.
        Resolve("bob@nightware.localhost", capability: JmapCapabilities.Submission).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public void Own_address_without_a_primary_account_is_not_found()
    {
        Resolve("alice@nightware.localhost", capability: "urn:example:none").Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Theory]
    [InlineData("bob", "alice@x.org", "bob@x.org")]
    [InlineData("bob@y.org", "alice@x.org", "bob@y.org")]
    [InlineData("bob", "alice", "bob")]
    [InlineData("bob", null, "bob")]
    [InlineData("", "alice@x.org", "")]
    public void Qualify_appends_the_username_domain_to_bare_names(string name, string? username, string expected)
    {
        JmapAccountResolver.Qualify(name, username).ShouldBe(expected);
    }
}

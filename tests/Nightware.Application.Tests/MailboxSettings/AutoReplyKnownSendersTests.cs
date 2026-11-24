using Nightware.Application.Common.Query;
using Nightware.Application.Contacts;
using Nightware.Application.MailboxSettings;
using Nightware.Domain.Contacts;
using Nightware.Domain.Kernel;
using Nightware.Domain.MailboxSettings;

namespace Nightware.Application.Tests.MailboxSettings;

public sealed class AutoReplyKnownSendersTests
{
    private static readonly MailboxRef Alice = new("alice@acme.example");
    private readonly IContactStore _contacts = Substitute.For<IContactStore>();
    private readonly IAutoReplyStore _replies = Substitute.For<IAutoReplyStore>();
    private readonly IInternalDomains _domains = Substitute.For<IInternalDomains>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Contact With(params string[] addresses) =>
        new(new ContactId(Guid.NewGuid().ToString("N")), null) { EmailAddresses = [.. addresses.Select(static a => new EmailAddress(a))] };

    [Fact]
    public async Task Contacts_addresses_outside_the_organisation_each_once_across_pages()
    {
        _contacts.ListAsync(Alice, Arg.Is<ItemQuery>(q => q.SkipToken == null), Ct)
            .Returns(new Page<Contact>([With("Sam@Supplier.example", "bob@acme.example"), With("pat@client.example")], "p2"));
        _contacts.ListAsync(Alice, Arg.Is<ItemQuery>(q => q.SkipToken == "p2"), Ct)
            .Returns(new Page<Contact>([With("sam@supplier.example", "evil\"]; discard; #@x.example", "bad\r\naddress@x.example")], null));

        var known = (await new AutoReplyKnownSenders(_contacts).ExecuteForTestAsync(Alice, ["acme.example"], Ct)).Value;

        known.ShouldBe(["sam@supplier.example", "pat@client.example"]);
    }

    [Theory]
    [InlineData("a@b.c", "a@b.c")]
    [InlineData("  Pat@Client.Example ", "pat@client.example")]
    [InlineData("no-at-sign", null)]
    [InlineData("two@@x", null)]
    [InlineData("@x", null)]
    [InlineData("x@", null)]
    [InlineData("a\"b@x", null)]
    [InlineData("a b@x", null)]
    [InlineData("a\\b@x", null)]
    [InlineData("a,b@x", null)]
    public void An_address_goes_into_the_filter_only_when_it_is_safe(string address, string? expected) =>
        AutoReplyKnownSenders.Normalise(address).ShouldBe(expected);

    [Fact]
    public async Task Contacts_that_cant_be_read_are_an_error()
    {
        _contacts.ListAsync(Alice, Arg.Any<ItemQuery>(), Ct).Returns(Error.Forbidden("no"));

        (await new AutoReplyKnownSenders(_contacts).ExecuteForTestAsync(Alice, [], Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task A_contact_change_refreshes_a_contacts_only_reply_and_leaves_others_alone()
    {
        var contactsOnly = new AutoReplySetting(AutoReplyStatus.AlwaysEnabled) { ExternalAudience = ExternalAudience.ContactsOnly };
        _replies.GetAsync(Alice, Ct).Returns(contactsOnly);
        _domains.ForAsync(Alice, Ct).Returns(["acme.example"]);
        _contacts.ListAsync(Alice, Arg.Any<ItemQuery>(), Ct).Returns(new Page<Contact>([With("pat@client.example")], null));
        var refresh = new RefreshAutoReplyKnownSenders(_replies, new AutoReplyKnownSenders(_contacts), _domains);

        await refresh.ExecuteAsync(Alice, Ct);
        await _replies.Received(1).SetAsync(Alice, contactsOnly, Arg.Any<IReadOnlyList<string>>(), Arg.Is<IReadOnlyList<string>?>(k => k!.SequenceEqual(new[] { "pat@client.example" })), Ct);

        _replies.ClearReceivedCalls();
        _replies.GetAsync(Alice, Ct).Returns(new AutoReplySetting(AutoReplyStatus.AlwaysEnabled) { ExternalAudience = ExternalAudience.All });
        await refresh.ExecuteAsync(Alice, Ct);
        await _replies.DidNotReceiveWithAnyArgs().SetAsync(default, null!, null!, null, Ct);
    }

    [Fact]
    public async Task Creating_or_deleting_a_contact_lets_the_reply_follow_and_a_failing_refresh_doesnt_fail_it()
    {
        var created = With("pat@client.example");
        _contacts.CreateAsync(Alice, null, Arg.Any<ContactDraft>(), Ct).Returns(created);
        _contacts.DeleteAsync(Alice, created.Id, Ct).Returns(Result.Success());
        _replies.GetAsync(Alice, Ct).Returns<Task<Result<AutoReplySetting>>>(_ => throw new InvalidOperationException("mail server gone"));
        var refresh = new RefreshAutoReplyKnownSenders(_replies, new AutoReplyKnownSenders(_contacts), _domains);

        (await new CreateContact(_contacts, refresh).ExecuteAsync(Alice, null, new ContactDraft { GivenName = "Pat" }, Ct)).Value.ShouldBeSameAs(created);
        (await new DeleteContact(_contacts, refresh).ExecuteAsync(Alice, created.Id, Ct)).IsSuccess.ShouldBeTrue();

        await _replies.Received(2).GetAsync(Alice, Ct);
    }
}

internal static class AutoReplyKnownSendersTestExtensions
{
    public static Task<Result<IReadOnlyList<string>>> ExecuteForTestAsync(this AutoReplyKnownSenders senders, MailboxRef mailbox, IReadOnlyList<string> internalDomains, CancellationToken cancellationToken) =>
        senders.ForAsync(mailbox, internalDomains, cancellationToken);
}

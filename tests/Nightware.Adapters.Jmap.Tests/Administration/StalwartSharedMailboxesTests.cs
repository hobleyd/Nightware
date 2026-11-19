using Nightware.Adapters.Jmap.Administration;
using Nightware.Adapters.Jmap.Tests.Support;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Tests.Administration;

/// <summary>Request and response shapes recorded from Stalwart 1.1.0 (phase 10 step 0).</summary>
public sealed class StalwartSharedMailboxesTests : IAsyncDisposable
{
    private const string Support = """{"name":"support","domainId":"d7","description":"Support","createdAt":"2026-11-19T08:49:10Z","memberTenantId":null,"roles":{"@type":"Default"},"quotas":{},"permissions":{"@type":"Inherit"},"aliases":{"0":{"enable":true,"name":"help","domainId":"d8"}},"@type":"Group","usedDiskQuota":0,"emailAddress":"support@acme.example","id":"f"}""";
    private const string Bob = """{"name":"bob","domainId":"d7","roles":{"@type":"User"},"permissions":{"@type":"Inherit"},"@type":"User","emailAddress":"bob@acme.example","id":"d"}""";
    private const string Carol = """{"emailAddress":"carol@acme.example","id":"c"}""";
    private const string NoMembers = """[["x:Account/query",{"ids":[],"position":0,"queryState":"q"},"c0"],["x:Account/get",{"state":"s","list":[],"notFound":[]},"c1"]]""";
    private readonly StalwartManagementTestHost _host = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ISharedMailAccounts Mailboxes() => _host.Get<ISharedMailAccounts>();

    private static MailAddress Address(string value) => MailAddress.TryParse(value, out var address) ? address : throw new ArgumentException(value);

    private void StubAcme() => _host.Stub("x:Domain/query", StalwartManagementTestHost.DomainLookup(("d7", "acme.example")), priority: 5);

    private void StubNamed(params string[] accounts) => _host.StubWhen("x:Account/query", "\"name\"", StalwartManagementTestHost.AccountPage(accounts), priority: 3);

    private void StubMembers(params string[] accounts) =>
        _host.StubWhen("x:Account/query", "memberGroupIds", accounts.Length == 0 ? NoMembers : StalwartManagementTestHost.AccountPage(accounts), priority: 2);

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    [Fact]
    public void Is_registered_as_the_port() => Mailboxes().ShouldBeOfType<StalwartSharedMailboxes>();

    [Fact]
    public async Task Finds_a_group_account_by_name_and_domain_with_its_aliases_and_members()
    {
        StubAcme();
        StubNamed(Support);
        StubMembers(Bob, Carol);
        _host.Stub("x:Domain/get", """[["x:Domain/get",{"list":[{"id":"d8","name":"acme.org"}],"notFound":[]},"c0"]]""");

        SharedMailAccount mailbox = (await Mailboxes().FindByAddressAsync(Address("support@acme.example"), Ct)).Value;

        mailbox.Id.ShouldBe("f");
        mailbox.Address.ShouldBe("support@acme.example");
        mailbox.Name.ShouldBe("Support");
        mailbox.Aliases.ShouldBe(["help@acme.org"]);
        mailbox.Members.ShouldBe([new SharedMailMember("d", "bob@acme.example"), new SharedMailMember("c", "carol@acme.example")]);
        mailbox.IsPrivileged.ShouldBeFalse();
        var named = _host.Calls("x:Account/query")[0][1]!["filter"]!;
        named["name"]!.GetValue<string>().ShouldBe("support");
        named["domainId"]!.GetValue<string>().ShouldBe("d7");
        _host.Calls("x:Account/query")[^1][1]!["filter"]!["memberGroupIds"]!.GetValue<string>().ShouldBe("f");
    }

    [Fact]
    public async Task A_person_s_mailbox_at_the_address_is_not_a_shared_mailbox()
    {
        StubAcme();
        StubNamed(Bob);

        (await Mailboxes().FindByAddressAsync(Address("bob@acme.example"), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task A_group_with_a_role_of_its_own_is_privileged()
    {
        StubAcme();
        StubNamed(Support.Replace("""{"@type":"Default"}""", """{"@type":"Admin"}""", StringComparison.Ordinal));
        StubMembers();
        _host.Stub("x:Domain/get", """[["x:Domain/get",{"list":[{"id":"d8","name":"acme.org"}],"notFound":[]},"c0"]]""");

        (await Mailboxes().FindByAddressAsync(Address("support@acme.example"), Ct)).Value.IsPrivileged.ShouldBeTrue();
    }

    [Fact]
    public async Task Members_are_read_page_by_page()
    {
        await using var host = new StalwartManagementTestHost(c => c["StalwartManagement:AccountPageSize"] = "1");
        host.Stub("x:Domain/query", StalwartManagementTestHost.DomainLookup(("d7", "acme.example")), priority: 5);
        host.StubWhen("x:Account/query", "\"name\"", StalwartManagementTestHost.AccountPage(Support.Replace("""{"0":{"enable":true,"name":"help","domainId":"d8"}}""", "{}", StringComparison.Ordinal)), priority: 3);
        host.StubWhen("x:Account/query", "\"position\":0,", StalwartManagementTestHost.AccountPage(Bob), priority: 1);
        host.StubWhen("x:Account/query", "\"position\":1,", StalwartManagementTestHost.AccountPage(Carol), priority: 1);
        host.StubWhen("x:Account/query", "\"position\":2,", NoMembers, priority: 1);

        SharedMailAccount mailbox = (await host.Get<ISharedMailAccounts>().FindByAddressAsync(Address("support@acme.example"), Ct)).Value;

        mailbox.Members.Select(static m => m.Address).ShouldBe(["bob@acme.example", "carol@acme.example"]);
    }

    [Fact]
    public async Task Creates_a_group_account_named_for_its_identity()
    {
        StubAcme();
        _host.Stub("x:Account/set", """[["x:Account/set",{"accountId":"d333333","created":{"g":{"id":"f"}}},"c0"]]""");

        SharedMailAccount created = (await Mailboxes().CreateAsync(Address("support@acme.example"), [Address("help@acme.example")], "Support", Ct)).Value;

        created.Id.ShouldBe("f");
        var account = _host.Calls("x:Account/set").Single()[1]!["create"]!["g"]!;
        account["@type"]!.GetValue<string>().ShouldBe("Group");
        account["name"]!.GetValue<string>().ShouldBe("support");
        account["domainId"]!.GetValue<string>().ShouldBe("d7");
        account["description"]!.GetValue<string>().ShouldBe("Support");
        account["aliases"]!["0"]!["name"]!.GetValue<string>().ShouldBe("help");
    }

    [Fact]
    public async Task An_address_someone_holds_is_a_conflict_naming_the_holder()
    {
        StubAcme();
        _host.Stub("x:Account/set", """[["x:Account/set",{"notCreated":{"g":{"type":"primaryKeyViolation","properties":["email"],"objectId":{"object":"MailingList","id":"b"}}}},"c0"]]""");

        Error error = (await Mailboxes().CreateAsync(Address("support@acme.example"), [], "Support", Ct)).Error!;

        error.Kind.ShouldBe(ErrorKind.Conflict);
        error.Message.ShouldContain("a list");
    }

    [Fact]
    public async Task Updates_aliases_and_name()
    {
        StubAcme();
        _host.Stub("x:Account/set", """[["x:Account/set",{"updated":{"f":null}},"c0"]]""");

        (await Mailboxes().UpdateAsync("f", [Address("help@acme.example")], "Help desk", Ct)).IsSuccess.ShouldBeTrue();

        var patch = _host.Calls("x:Account/set").Single()[1]!["update"]!["f"]!;
        patch["description"]!.GetValue<string>().ShouldBe("Help desk");
        patch["aliases"]!["0"]!["domainId"]!.GetValue<string>().ShouldBe("d7");
    }

    [Fact]
    public async Task Membership_is_patched_one_group_at_a_time()
    {
        _host.Stub("x:Account/set", """[["x:Account/set",{"updated":{"d":null}},"c0"]]""");

        (await Mailboxes().AddMemberAsync("f", "d", Ct)).IsSuccess.ShouldBeTrue();
        (await Mailboxes().RemoveMemberAsync("f", "d", Ct)).IsSuccess.ShouldBeTrue();

        var patches = _host.Calls("x:Account/set").Select(static c => c[1]!["update"]!["d"]!.AsObject()).ToList();
        patches[0].ToJsonString().ShouldBe("""{"memberGroupIds/f":true}""");
        patches[1].ToJsonString().ShouldBe("""{"memberGroupIds/f":null}""");
    }

    [Fact]
    public async Task Destroying_takes_every_member_out_first_in_the_same_request()
    {
        _host.StubWhen("x:Account/get", "\"ids\":[\"f\"]", """[["x:Account/get",{"list":[{"@type":"Group","roles":{"@type":"Default"},"permissions":{"@type":"Inherit"},"id":"f"}],"notFound":[]},"c0"]]""");
        StubMembers(Bob, Carol);
        _host.Stub("x:Account/set", """[["x:Account/set",{"updated":{"d":null,"c":null}},"c0"],["x:Account/set",{"destroyed":["f"]},"c1"]]""");

        (await Mailboxes().DestroyAsync("f", Ct)).IsSuccess.ShouldBeTrue();

        var sets = _host.Calls("x:Account/set");
        sets[0][1]!["update"]!.AsObject().Select(static p => p.Key).ShouldBe(["d", "c"]);
        sets[0][1]!["update"]!["c"]!.ToJsonString().ShouldBe("""{"memberGroupIds/f":null}""");
        sets[1][1]!["destroy"]!.AsArray().Single()!.GetValue<string>().ShouldBe("f");
        _host.Logs.AllText.ShouldContain("Destroyed Stalwart shared mailbox f after taking out 2 member(s)");
    }

    [Fact]
    public async Task Never_destroys_a_person_s_mailbox_and_an_absent_one_is_not_found()
    {
        _host.StubWhen("x:Account/get", "\"ids\":[\"d\"]", """[["x:Account/get",{"list":[{"@type":"User","roles":{"@type":"User"},"permissions":{"@type":"Inherit"},"id":"d"}],"notFound":[]},"c0"]]""");
        _host.StubWhen("x:Account/get", "\"ids\":[\"z\"]", """[["x:Account/get",{"list":[],"notFound":["z"]},"c0"]]""");

        (await Mailboxes().DestroyAsync("d", Ct)).Error!.Kind.ShouldBe(ErrorKind.Conflict);
        (await Mailboxes().DestroyAsync("z", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _host.Calls("x:Account/set").ShouldBeEmpty();
    }
}

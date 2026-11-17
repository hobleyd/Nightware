using Nightware.Adapters.Jmap.Administration;
using Nightware.Adapters.Jmap.Tests.Support;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Tests.Administration;

public sealed class StalwartMailAccountsTests : IAsyncDisposable
{
    private const string Carol = """{"id":"a3","@type":"User","name":"carol","domainId":"d7","emailAddress":"carol@acme.example","roles":{"@type":"User"},"permissions":{"@type":"Inherit"},"aliases":{"0":{"enable":true,"name":"c.smith","domainId":"d7"},"1":{"name":"carol","domainId":"d8"}}}""";
    private StalwartManagementTestHost _host = new(s => s["StalwartManagement:AccountPageSize"] = "2");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IMailAccounts Accounts() => _host.Get<IMailAccounts>();

    private static MailAddress Address(string value) => MailAddress.TryParse(value, out var address) ? address : throw new ArgumentException(value);

    private void StubAcme() => _host.Stub("x:Domain/query", StalwartManagementTestHost.DomainLookup(("d7", "acme.example")), priority: 5);

    private void StubOtherDomains() =>
        _host.Stub("x:Domain/get", """[["x:Domain/get",{"list":[{"id":"d8","name":"acme.org"}],"notFound":[]},"c0"]]""");

    [Fact]
    public void Is_registered_as_the_port()
    {
        Accounts().ShouldBeOfType<StalwartMailAccounts>();
    }

    [Fact]
    public async Task Finds_by_primary_address_with_the_name_and_domain_filter()
    {
        StubAcme();
        _host.Stub("x:Account/query", StalwartManagementTestHost.AccountPage(Carol));
        StubOtherDomains();

        var account = (await Accounts().FindByAddressAsync("Carol@Acme.example", Ct)).Value;

        account.Id.ShouldBe("a3");
        account.Address.ShouldBe("carol@acme.example");
        account.IsIndividual.ShouldBeTrue();
        account.IsPrivileged.ShouldBeFalse();
        account.Aliases.ShouldBe(["c.smith@acme.example", "carol@acme.org"]);
        var query = _host.Calls("x:Account/query").Single();
        query[1]!["filter"]!["name"]!.GetValue<string>().ShouldBe("carol");
        query[1]!["filter"]!["domainId"]!.GetValue<string>().ShouldBe("d7");
        var get = _host.Calls("x:Account/get").Single();
        get[1]!["properties"]!.AsArray().Select(p => p!.GetValue<string>()).ShouldBe(["@type", "name", "domainId", "emailAddress", "aliases", "roles", "permissions"]);
        get[1]!["#ids"]!["resultOf"]!.GetValue<string>().ShouldBe("c0");
        _host.Calls("x:Domain/get")[^1][1]!["ids"]!.AsArray().Select(i => i!.GetValue<string>()).ShouldBe(["d8"]);
    }

    [Theory]
    [InlineData(",\"roles\":{\"@type\":\"Admin\"}", true)]
    [InlineData(",\"roles\":{\"@type\":\"Custom\",\"roleIds\":{\"r1\":true}}", true)]
    [InlineData(",\"permissions\":{\"@type\":\"Merge\",\"enabledPermissions\":{\"impersonate\":true}}", true)]
    [InlineData(",\"roles\":{\"@type\":\"User\"},\"permissions\":{\"@type\":\"Inherit\"}", false)]
    [InlineData("", true)] // fails closed: Stalwart always sends both fields
    [InlineData(",\"roles\":{\"@type\":\"User\"}", true)]
    public async Task Accounts_with_more_than_user_rights_are_privileged(string extra, bool privileged)
    {
        StubAcme();
        string account = "{\"id\":\"a9\",\"@type\":\"User\",\"name\":\"nightware-admin\",\"domainId\":\"d7\",\"emailAddress\":\"nightware-admin@acme.example\"" + extra + "}";
        _host.Stub("x:Account/query", StalwartManagementTestHost.AccountPage(account));
        StubOtherDomains();

        (await Accounts().FindByAddressAsync("nightware-admin@acme.example", Ct)).Value.IsPrivileged.ShouldBe(privileged);
    }

    [Fact]
    public async Task Finds_by_alias_by_paging_through_accounts()
    {
        const string bob = """{"id":"a2","@type":"User","name":"bob","domainId":"d7","emailAddress":"bob@acme.example"}""";
        const string dave = """{"id":"a4","@type":"User","name":"dave","domainId":"d7","emailAddress":"dave@acme.example","aliases":[]}""";
        StubAcme();
        _host.StubWhen("x:Account/query", "\"name\":\"c.smith\"", StalwartManagementTestHost.AccountPage(), priority: 0);
        _host.StubWhen("x:Account/query", "\"position\":0,", StalwartManagementTestHost.AccountPage(bob, dave));
        _host.StubWhen("x:Account/query", "\"position\":2,", StalwartManagementTestHost.AccountPage(Carol));
        StubOtherDomains();

        var account = (await Accounts().FindByAddressAsync("c.smith@acme.example", Ct)).Value;

        account.Id.ShouldBe("a3");
        account.Address.ShouldBe("carol@acme.example");
        var queries = _host.Calls("x:Account/query");
        queries.Count.ShouldBe(3);
        queries[1][1]!.AsObject().ContainsKey("filter").ShouldBeFalse();
        queries[1][1]!["limit"]!.GetValue<int>().ShouldBe(2);
        queries[2][1]!["position"]!.GetValue<int>().ShouldBe(2);
    }

    [Fact]
    public async Task Keeps_paging_when_the_server_caps_the_page_size()
    {
        const string bob = """{"id":"a2","@type":"User","name":"bob","domainId":"d7","emailAddress":"bob@acme.example"}""";
        StubAcme();
        _host.StubWhen("x:Account/query", "\"name\":\"c.smith\"", StalwartManagementTestHost.AccountPage(), priority: 0);
        _host.StubWhen("x:Account/query", "\"position\":0,", Capped(StalwartManagementTestHost.AccountPage(bob)));
        _host.StubWhen("x:Account/query", "\"position\":1,", Capped(StalwartManagementTestHost.AccountPage(Carol)));
        StubOtherDomains();

        (await Accounts().FindByAddressAsync("c.smith@acme.example", Ct)).Value.Id.ShouldBe("a3");

        _host.Calls("x:Account/query")[2][1]!["position"]!.GetValue<int>().ShouldBe(1);

        static string Capped(string page) => page.Replace("\"queryState\":\"q\"", "\"queryState\":\"q\",\"limit\":1", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reads_aliases_sent_as_an_array()
    {
        const string erin = """{"id":"a5","@type":"User","name":"erin","domainId":"d7","emailAddress":"erin@acme.example","aliases":[{"name":"sales","domainId":"d7"}]}""";
        StubAcme();
        _host.StubWhen("x:Account/query", "\"name\":\"sales\"", StalwartManagementTestHost.AccountPage(), priority: 0);
        _host.StubWhen("x:Account/query", "\"position\":0,", StalwartManagementTestHost.AccountPage(erin));

        var account = (await Accounts().FindByAddressAsync("sales@acme.example", Ct)).Value;

        account.Id.ShouldBe("a5");
        account.Aliases.ShouldBe(["sales@acme.example"]);
    }

    [Fact]
    public async Task A_name_filter_hit_with_another_address_is_not_taken()
    {
        const string carolyn = """{"id":"a6","@type":"User","name":"carolyn","domainId":"d7","emailAddress":"carolyn@acme.example"}""";
        StubAcme();
        _host.StubWhen("x:Account/query", "\"name\":\"carol\"", StalwartManagementTestHost.AccountPage(carolyn), priority: 0);
        _host.StubWhen("x:Account/query", "\"position\":0,", StalwartManagementTestHost.AccountPage(carolyn));

        (await Accounts().FindByAddressAsync("carol@acme.example", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Theory]
    [InlineData("\"@type\":\"Group\",")]
    [InlineData("")]
    public async Task A_group_or_untyped_account_is_not_individual(string type)
    {
        string sales = $$"""{"id":"g1",{{type}}"name":"sales","domainId":"d7","emailAddress":"sales@acme.example"}""";
        StubAcme();
        _host.Stub("x:Account/query", StalwartManagementTestHost.AccountPage(sales));

        var account = (await Accounts().FindByAddressAsync("sales@acme.example", Ct)).Value;

        account.IsIndividual.ShouldBeFalse();
        account.Aliases.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_address_on_a_domain_the_server_does_not_serve_is_not_found()
    {
        _host.Stub("x:Domain/query", StalwartManagementTestHost.DomainLookup());

        (await Accounts().FindByAddressAsync("carol@elsewhere.example", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _host.Calls("x:Account/query").ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unknown_address_is_not_found_after_the_last_page()
    {
        StubAcme();
        _host.StubWhen("x:Account/query", "\"position\":0,", StalwartManagementTestHost.AccountPage("""{"id":"a2","@type":"User","emailAddress":"bob@acme.example"}"""));

        (await Accounts().FindByAddressAsync("nobody@acme.example", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task The_alias_scan_is_bounded()
    {
        await _host.DisposeAsync();
        _host = new StalwartManagementTestHost(s =>
        {
            s["StalwartManagement:AccountPageSize"] = "2";
            s["StalwartManagement:MaxAccountsScanned"] = "4";
        });
        StubAcme();
        _host.Stub("x:Account/query", StalwartManagementTestHost.AccountPage(
            """{"id":"x1","@type":"User","emailAddress":"x1@acme.example"}""",
            """{"id":"x2","@type":"User","emailAddress":"x2@acme.example"}"""));

        var found = await Accounts().FindByAddressAsync("nobody@acme.example", Ct);

        found.Error!.Kind.ShouldBe(ErrorKind.Unknown);
        found.Error.Message.ShouldContain("MaxAccountsScanned");
        _host.Calls("x:Account/query").Count.ShouldBe(3); // the name query, then two pages of two
    }

    [Fact]
    public async Task A_malformed_address_is_a_validation_error()
    {
        (await Accounts().FindByAddressAsync("not-an-address", Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public async Task Creates_a_user_in_the_addresses_domain()
    {
        StubAcme();
        _host.Stub("x:Account/set", """[["x:Account/set",{"created":{"a":{"id":"a9"}}},"c0"]]""");

        var account = (await Accounts().CreateAsync(Address("carol@acme.example"), "carol", Ct)).Value;

        account.ShouldBe(new MailAccount("a9", "carol@acme.example", IsIndividual: true));
        var create = _host.Calls("x:Account/set").Single()[1]!["create"]!["a"]!.AsObject();
        create["@type"]!.GetValue<string>().ShouldBe("User");
        create["name"]!.GetValue<string>().ShouldBe("carol");
        create["domainId"]!.GetValue<string>().ShouldBe("d7");
        create["description"]!.GetValue<string>().ShouldBe("carol");
        create.ContainsKey("aliases").ShouldBeFalse();
    }

    [Theory]
    [InlineData("alreadyExists", ErrorKind.Conflict)]
    [InlineData("forbidden", ErrorKind.Forbidden)]
    [InlineData("invalidProperties", ErrorKind.Validation)]
    public async Task Maps_a_refused_create(string type, ErrorKind kind)
    {
        StubAcme();
        _host.Stub("x:Account/set", $$$$"""[["x:Account/set",{"notCreated":{"a":{"type":"{{{{type}}}}","properties":["name"]}}},"c0"]]""");

        (await Accounts().CreateAsync(Address("carol@acme.example"), "carol", Ct)).Error!.Kind.ShouldBe(kind);
    }

    [Fact]
    public async Task Creating_on_a_domain_the_server_does_not_serve_is_a_validation_error()
    {
        _host.Stub("x:Domain/query", StalwartManagementTestHost.DomainLookup());

        (await Accounts().CreateAsync(Address("carol@acme.example"), "carol", Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        _host.Calls("x:Account/set").ShouldBeEmpty();
    }

    [Fact]
    public async Task Replaces_the_aliases_as_an_index_keyed_map()
    {
        _host.StubWhen("x:Domain/query", "\"name\":\"acme.example\"", StalwartManagementTestHost.DomainLookup(("d7", "acme.example")));
        _host.StubWhen("x:Domain/query", "\"name\":\"acme.org\"", StalwartManagementTestHost.DomainLookup(("d8", "acme.org")));
        _host.Stub("x:Account/set", """[["x:Account/set",{"updated":{"a3":null}},"c0"]]""");

        var result = await Accounts().SetAliasesAsync("a3", [Address("c.smith@acme.example"), Address("carol@acme.org")], Ct);

        result.IsSuccess.ShouldBeTrue();
        var update = _host.Calls("x:Account/set").Single()[1]!["update"]!["a3"]!.AsObject();
        update.ToJsonString().ShouldBe("""{"aliases":{"0":{"name":"c.smith","domainId":"d7"},"1":{"name":"carol","domainId":"d8"}}}""");
    }

    [Fact]
    public async Task Clearing_the_aliases_sends_an_empty_map()
    {
        _host.Stub("x:Account/set", """[["x:Account/set",{"updated":{"a3":null}},"c0"]]""");

        (await Accounts().SetAliasesAsync("a3", [], Ct)).IsSuccess.ShouldBeTrue();

        _host.Calls("x:Account/set").Single()[1]!["update"]!["a3"]!.ToJsonString().ShouldBe("""{"aliases":{}}""");
        _host.Calls("x:Domain/query").ShouldBeEmpty();
    }

    [Theory]
    [InlineData("notFound", ErrorKind.NotFound)]
    [InlineData("invalidPatch", ErrorKind.Validation)]
    [InlineData("alreadyExists", ErrorKind.Conflict)]
    [InlineData("forbidden", ErrorKind.Forbidden)]
    public async Task Maps_a_refused_alias_update(string type, ErrorKind kind)
    {
        StubAcme();
        _host.Stub("x:Account/set", $$$$"""[["x:Account/set",{"notUpdated":{"a3":{"type":"{{{{type}}}}"}}},"c0"]]""");

        (await Accounts().SetAliasesAsync("a3", [Address("sales@acme.example")], Ct)).Error!.Kind.ShouldBe(kind);
    }

    [Fact]
    public async Task An_alias_on_an_unserved_domain_is_a_validation_error()
    {
        _host.Stub("x:Domain/query", StalwartManagementTestHost.DomainLookup());

        (await Accounts().SetAliasesAsync("a3", [Address("sales@elsewhere.example")], Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        _host.Calls("x:Account/set").ShouldBeEmpty();
    }

    [Fact]
    public async Task Reads_the_used_disk_quota()
    {
        _host.Stub("x:Account/get", """[["x:Account/get",{"list":[{"id":"a3","usedDiskQuota":123456789}],"notFound":[]},"c0"]]""");

        (await Accounts().GetUsageAsync("a3", Ct)).Value.ShouldBe(123456789L);

        var get = _host.Calls("x:Account/get").Single()[1]!;
        get["ids"]!.AsArray().Single()!.GetValue<string>().ShouldBe("a3");
        get["properties"]!.AsArray().Single()!.GetValue<string>().ShouldBe("usedDiskQuota");
    }

    [Fact]
    public async Task Usage_of_a_missing_account_is_not_found()
    {
        _host.Stub("x:Account/get", """[["x:Account/get",{"list":[],"notFound":["a3"]},"c0"]]""");

        (await Accounts().GetUsageAsync("a3", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Unreported_usage_is_an_error()
    {
        _host.Stub("x:Account/get", """[["x:Account/get",{"list":[{"id":"a3"}],"notFound":[]},"c0"]]""");

        (await Accounts().GetUsageAsync("a3", Ct)).Error!.Kind.ShouldBe(ErrorKind.Unknown);
    }

    [Fact]
    public async Task Destroys_the_account()
    {
        _host.Stub("x:Account/set", """[["x:Account/set",{"destroyed":["a3"]},"c0"]]""");

        (await Accounts().DestroyAsync("a3", Ct)).IsSuccess.ShouldBeTrue();

        _host.Calls("x:Account/set").Single()[1]!["destroy"]!.AsArray().Single()!.GetValue<string>().ShouldBe("a3");
        _host.Logs.AllText.ShouldContain("Destroyed Stalwart account a3");
    }

    [Theory]
    [InlineData("notFound", ErrorKind.NotFound)]
    [InlineData("forbidden", ErrorKind.Forbidden)]
    public async Task Maps_a_refused_destroy(string type, ErrorKind kind)
    {
        _host.Stub("x:Account/set", $$$$"""[["x:Account/set",{"notDestroyed":{"a3":{"type":"{{{{type}}}}"}}},"c0"]]""");

        (await Accounts().DestroyAsync("a3", Ct)).Error!.Kind.ShouldBe(kind);
    }

    [Fact]
    public async Task A_method_error_on_destroy_is_returned()
    {
        _host.Stub("x:Account/set", """[["error",{"type":"forbidden","description":"sysAccountDestroy"},"c0"]]""");

        (await Accounts().DestroyAsync("a3", Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();
}
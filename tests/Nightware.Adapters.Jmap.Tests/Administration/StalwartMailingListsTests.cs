using Nightware.Adapters.Jmap.Administration;
using Nightware.Adapters.Jmap.Tests.Support;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Tests.Administration;

/// <summary>Request and response shapes recorded from Stalwart 1.1.0 (phase 9 step 0 b, c).</summary>
public sealed class StalwartMailingListsTests : IAsyncDisposable
{
    private const string Finance = """{"id":"b","name":"finance","domainId":"d7","emailAddress":"finance@acme.example","aliases":{"0":{"enable":true,"name":"accounts","domainId":"d8"}},"recipients":{"carol@acme.example":true,"accountant@example.net":true,"gone@acme.example":false}}""";
    private readonly StalwartManagementTestHost _host = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IMailingLists Lists() => _host.Get<IMailingLists>();

    private static MailAddress Address(string value) => MailAddress.TryParse(value, out var address) ? address : throw new ArgumentException(value);

    private void StubAcme() => _host.Stub("x:Domain/query", StalwartManagementTestHost.DomainLookup(("d7", "acme.example")), priority: 5);

    private void StubLists(params string[] lists)
    {
        string ids = string.Join(',', lists.Select(static l => System.Text.Json.Nodes.JsonNode.Parse(l)!["id"]!.ToJsonString()));
        _host.Stub("x:MailingList/query", $$"""[["x:MailingList/query",{"ids":[{{ids}}],"position":0,"queryState":"q"},"c0"],["x:MailingList/get",{"state":"s","list":[{{string.Join(',', lists)}}],"notFound":[]},"c1"]]""");
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    [Fact]
    public void Is_registered_as_the_port() => Lists().ShouldBeOfType<StalwartMailingLists>();

    [Fact]
    public async Task Finds_a_list_by_text_and_confirms_its_address()
    {
        StubAcme();
        StubLists("""{"id":"x","name":"finance-old","domainId":"d7","emailAddress":"finance-old@acme.example"}""", Finance);
        _host.Stub("x:Domain/get", """[["x:Domain/get",{"list":[{"id":"d8","name":"acme.org"}],"notFound":[]},"c0"]]""");

        MailingList list = (await Lists().FindByAddressAsync(Address("finance@acme.example"), Ct)).Value;

        list.Id.ShouldBe("b");
        list.Address.ShouldBe("finance@acme.example");
        list.Aliases.ShouldBe(["accounts@acme.org"]);
        list.Recipients.ShouldBe(["carol@acme.example", "accountant@example.net"]);
        var query = _host.Calls("x:MailingList/query").Single();
        query[1]!["filter"]!["text"]!.GetValue<string>().ShouldBe("finance");
        _host.Calls("x:MailingList/get").Single()[1]!["#ids"]!["resultOf"]!.GetValue<string>().ShouldBe("c0");
    }

    [Fact]
    public async Task No_list_or_no_domain_is_not_found()
    {
        StubAcme();
        StubLists();
        (await Lists().FindByAddressAsync(Address("finance@acme.example"), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);

        _host.Stub("x:Domain/query", StalwartManagementTestHost.DomainLookup(), priority: 1);
        (await Lists().FindByAddressAsync(Address("finance@nowhere.example"), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Creates_a_list_with_aliases_as_an_index_map_and_recipients_as_a_set()
    {
        StubAcme();
        _host.Stub("x:MailingList/set", """[["x:MailingList/set",{"created":{"l":{"id":"b"}}},"c0"]]""");

        MailingList list = (await Lists().CreateAsync(Address("finance@acme.example"), [Address("accounts@acme.example")], ["Carol@acme.example", "accountant@example.net"], "Acme: Finance", Ct)).Value;

        list.Id.ShouldBe("b");
        var created = _host.Calls("x:MailingList/set").Single()[1]!["create"]!["l"]!;
        created["name"]!.GetValue<string>().ShouldBe("finance");
        created["domainId"]!.GetValue<string>().ShouldBe("d7");
        created["description"]!.GetValue<string>().ShouldBe("Acme: Finance");
        created["aliases"]!["0"]!["name"]!.GetValue<string>().ShouldBe("accounts");
        created["recipients"]!.AsObject().Select(static p => p.Key).ShouldBe(["carol@acme.example", "accountant@example.net"]);
        created["recipients"]!["carol@acme.example"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Theory]
    [InlineData("Account", "a mailbox")]
    [InlineData("MailingList", "a list")]
    public async Task An_address_someone_holds_is_a_conflict_naming_the_holder(string holder, string named)
    {
        StubAcme();
        _host.Stub("x:MailingList/set", $$$"""[["x:MailingList/set",{"notCreated":{"l":{"type":"primaryKeyViolation","properties":["email"],"objectId":{"object":"{{{holder}}}","id":"c"} } } },"c0"]]""");

        Error error = (await Lists().CreateAsync(Address("alice@acme.example"), [], [], "x", Ct)).Error!;

        error.Kind.ShouldBe(ErrorKind.Conflict);
        error.Message.ShouldContain(named);
    }

    [Fact]
    public async Task A_domain_the_mail_server_does_not_serve_is_refused_before_writing()
    {
        _host.Stub("x:Domain/query", StalwartManagementTestHost.DomainLookup(), priority: 5);

        (await Lists().CreateAsync(Address("finance@nowhere.example"), [], [], "x", Ct)).Error!.Target.ShouldBe("domain");
        _host.Calls("x:MailingList/set").ShouldBeEmpty();
    }

    [Fact]
    public async Task Updating_replaces_aliases_and_recipients()
    {
        _host.Stub("x:MailingList/set", """[["x:MailingList/set",{"updated":{"b":null}},"c0"]]""");

        (await Lists().UpdateAsync("b", [], ["dave@acme.example"], Ct)).IsSuccess.ShouldBeTrue();

        var update = _host.Calls("x:MailingList/set").Single()[1]!["update"]!["b"]!;
        update["aliases"]!.AsObject().Count.ShouldBe(0);
        update["recipients"]!.AsObject().Select(static p => p.Key).ShouldBe(["dave@acme.example"]);
    }

    [Fact]
    public async Task An_alias_someone_holds_is_a_conflict_on_update()
    {
        StubAcme();
        _host.Stub("x:MailingList/set", """[["x:MailingList/set",{"notUpdated":{"b":{"type":"primaryKeyViolation","properties":["email"],"objectId":{"object":"Account","id":"c"}}}},"c0"]]""");

        (await Lists().UpdateAsync("b", [Address("alice@acme.example")], [], Ct)).Error!.Kind.ShouldBe(ErrorKind.Conflict);
    }

    [Fact]
    public async Task Destroying_a_list_that_is_gone_is_not_found()
    {
        _host.Stub("x:MailingList/set", """[["x:MailingList/set",{"notDestroyed":{"b":{"type":"notFound"}}},"c0"]]""");
        (await Lists().DestroyAsync("b", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task The_first_sender_rule_creates_the_script_and_reloads()
    {
        _host.Stub("x:SieveSystemScript/query", """[["x:SieveSystemScript/query",{"ids":[],"position":0,"queryState":"q"},"c0"],["x:SieveSystemScript/get",{"list":[],"notFound":[]},"c1"]]""");
        _host.Stub("x:SieveSystemScript/set", """[["x:SieveSystemScript/set",{"created":{"s":{"id":"jp1"}}},"c0"],["x:Action/set",{"created":{"r":{"id":"r1"}}},"c1"]]""");
        SenderRule rule = new([Address("finance@acme.example")], ["bob@acme.example"], []);

        (await Lists().SetSenderRuleAsync(Address("finance@acme.example"), rule, Ct)).IsSuccess.ShouldBeTrue();

        var created = _host.Calls("x:SieveSystemScript/set").Single()[1]!["create"]!["s"]!;
        created["name"]!.GetValue<string>().ShouldBe("nightware-lists");
        created["isActive"]!.GetValue<bool>().ShouldBeTrue();
        created["contents"]!.GetValue<string>().ShouldContain("""not envelope :is "from" ["bob@acme.example"]""");
        _host.Calls("x:Action/set").Single()[1]!["create"]!["r"]!["@type"]!.GetValue<string>().ShouldBe("ReloadSettings");
    }

    [Fact]
    public async Task An_unchanged_rule_writes_nothing()
    {
        SenderRule rule = new([Address("finance@acme.example")], ["bob@acme.example"], []);
        string contents = ListSenderScript.With(null, Address("finance@acme.example"), rule);
        string script = System.Text.Json.JsonSerializer.Serialize(new { id = "jp1", name = "nightware-lists", isActive = true, contents });
        _host.Stub("x:SieveSystemScript/query", $$"""[["x:SieveSystemScript/query",{"ids":["jp1"],"position":0,"queryState":"q"},"c0"],["x:SieveSystemScript/get",{"list":[{{script}}],"notFound":[]},"c1"]]""");

        (await Lists().SetSenderRuleAsync(Address("finance@acme.example"), rule, Ct)).IsSuccess.ShouldBeTrue();
        _host.Calls("x:SieveSystemScript/set").ShouldBeEmpty();
    }

    [Fact]
    public async Task Removing_a_rule_updates_the_existing_script()
    {
        SenderRule rule = new([Address("finance@acme.example")], ["bob@acme.example"], []);
        string contents = ListSenderScript.With(null, Address("finance@acme.example"), rule);
        string script = System.Text.Json.JsonSerializer.Serialize(new { id = "jp1", name = "nightware-lists", isActive = true, contents });
        _host.Stub("x:SieveSystemScript/query", $$"""[["x:SieveSystemScript/query",{"ids":["jp1"],"position":0,"queryState":"q"},"c0"],["x:SieveSystemScript/get",{"list":[{{script}}],"notFound":[]},"c1"]]""");
        _host.Stub("x:SieveSystemScript/set", """[["x:SieveSystemScript/set",{"updated":{"jp1":null}},"c0"],["x:Action/set",{"created":{"r":{"id":"r1"}}},"c1"]]""");

        (await Lists().SetSenderRuleAsync(Address("finance@acme.example"), null, Ct)).IsSuccess.ShouldBeTrue();

        _host.Calls("x:SieveSystemScript/set").Single()[1]!["update"]!["jp1"]!["contents"]!.GetValue<string>().ShouldNotContain("finance@acme.example");
    }
}

public sealed class ListSenderScriptTests
{
    private static MailAddress Address(string value) => MailAddress.TryParse(value, out var address) ? address : throw new ArgumentException(value);

    private static MailDomain Domain(string value) => MailDomain.TryParse(value, out var domain) ? domain : throw new ArgumentException(value);

    [Fact]
    public void A_members_rule_rejects_at_rcpt_with_5_7_1() =>
        ListSenderScript.With(null, Address("finance@acme.example"), new SenderRule([Address("finance@acme.example"), Address("accounts@acme.example")], ["bob@acme.example", "carol@acme.example"], []))
            .ShouldBe("""
                require ["envelope", "reject"];
                # nightware-list finance@acme.example
                if allof (envelope :is "to" ["finance@acme.example", "accounts@acme.example"],
                          not envelope :is "from" ["bob@acme.example", "carol@acme.example"]) {
                    reject "550 5.7.1 finance@acme.example accepts mail from its members only";
                }
                # end nightware-list finance@acme.example

                """.Replace("\r\n", "\n", StringComparison.Ordinal));

    [Fact]
    public void An_organisation_rule_tests_the_sender_s_domain()
    {
        string script = ListSenderScript.With(null, Address("all@acme.example"), new SenderRule([Address("all@acme.example")], [], [Domain("acme.example"), Domain("acme.org")]));

        script.ShouldContain("""not envelope :domain :is "from" ["acme.example", "acme.org"]""");
        script.ShouldContain("accepts mail from the organisation only");
    }

    [Fact]
    public void A_list_without_members_refuses_everyone()
    {
        string script = ListSenderScript.With(null, Address("empty@acme.example"), new SenderRule([Address("empty@acme.example")], [], []));

        script.ShouldContain("""if allof (envelope :is "to" ["empty@acme.example"]) {""");
    }

    [Fact]
    public void A_sender_that_would_need_escaping_is_left_out()
    {
        string script = ListSenderScript.With(null, Address("finance@acme.example"), new SenderRule([Address("finance@acme.example")], ["\"odd\"@acme.example", "bob@acme.example"], []));

        script.ShouldContain("""not envelope :is "from" ["bob@acme.example"]""");
        script.ShouldNotContain("odd");
    }

    [Fact]
    public void Each_list_s_block_is_replaced_or_removed_alone_and_the_rest_is_kept()
    {
        SenderRule finance = new([Address("finance@acme.example")], ["bob@acme.example"], []);
        SenderRule board = new([Address("board@acme.example")], ["carol@acme.example"], []);
        string script = ListSenderScript.With("require [\"envelope\", \"reject\"];\n# hand-written note\n", Address("finance@acme.example"), finance);
        script = ListSenderScript.With(script, Address("board@acme.example"), board);
        script = ListSenderScript.With(script, Address("finance@acme.example"), finance with { AllowedSenders = ["dave@acme.example"] });

        script.ShouldContain("# hand-written note");
        script.ShouldContain("dave@acme.example");
        script.ShouldNotContain("bob@acme.example");
        script.IndexOf("board@acme.example", StringComparison.Ordinal).ShouldBeLessThan(script.IndexOf("finance@acme.example", StringComparison.Ordinal));

        string removed = ListSenderScript.With(script, Address("finance@acme.example"), null);
        removed.ShouldNotContain("finance@acme.example");
        removed.ShouldContain("carol@acme.example");
        ListSenderScript.With(removed, Address("board@acme.example"), null).ShouldBe("require [\"envelope\", \"reject\"];\n# hand-written note\n");
    }

    [Fact]
    public void An_unterminated_block_is_dropped()
    {
        string script = ListSenderScript.With("require [\"envelope\", \"reject\"];\n# nightware-list x@acme.example\nif true {\n", Address("finance@acme.example"), null);

        script.ShouldBe("require [\"envelope\", \"reject\"];\n");
    }
}

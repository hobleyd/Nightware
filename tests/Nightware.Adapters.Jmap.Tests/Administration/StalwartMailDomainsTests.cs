using Nightware.Adapters.Jmap.Administration;
using Nightware.Adapters.Jmap.Tests.Support;
using Nightware.Application.Administration;
using Nightware.Domain.Administration;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Jmap.Tests.Administration;

public sealed class StalwartMailDomainsTests : IAsyncDisposable
{
    private static readonly MailDomain Acme = Parse("acme.example");
    private readonly StalwartManagementTestHost _host = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IMailDomains Domains() => _host.Get<IMailDomains>();

    private static MailDomain Parse(string name) => MailDomain.TryParse(name, out var domain) ? domain : throw new ArgumentException(name);

    private void StubDkim(params string[] ids) =>
        _host.Stub("x:DkimSignature/query", $$"""[["x:DkimSignature/query",{"ids":[{{string.Join(',', ids.Select(i => $"\"{i}\""))}}],"position":0},"c0"]]""");

    [Fact]
    public void Is_registered_as_the_port()
    {
        Domains().ShouldBeOfType<StalwartMailDomains>();
    }

    [Fact]
    public async Task Finds_a_domain_by_name_with_its_dkim_keys()
    {
        _host.Stub("x:Domain/query", StalwartManagementTestHost.DomainLookup(("d7", "acme.example")));
        StubDkim("k1", "k2");

        var info = (await Domains().FindAsync(Acme, Ct)).Value;

        info.ShouldBe(new MailDomainInfo("d7", Acme, DkimPresent: true));
        var calls = _host.Calls();
        calls[0][0]!.GetValue<string>().ShouldBe("x:Domain/query");
        calls[0][1]!["filter"]!["name"]!.GetValue<string>().ShouldBe("acme.example");
        calls[1][0]!.GetValue<string>().ShouldBe("x:Domain/get");
        calls[1][1]!["#ids"]!["resultOf"]!.GetValue<string>().ShouldBe("c0");
        calls[1][1]!["#ids"]!["path"]!.GetValue<string>().ShouldBe("/ids");
        calls[2][0]!.GetValue<string>().ShouldBe("x:DkimSignature/query");
        calls[2][1]!["filter"]!["domainId"]!.GetValue<string>().ShouldBe("d7");
    }

    [Fact]
    public async Task Reports_a_domain_without_dkim_keys()
    {
        _host.Stub("x:Domain/query", StalwartManagementTestHost.DomainLookup(("d7", "acme.example")));
        StubDkim();

        (await Domains().FindAsync(Acme, Ct)).Value.DkimPresent.ShouldBeFalse();
    }

    [Fact]
    public async Task Finds_a_domains_catch_all_address()
    {
        _host.Stub("x:Domain/query", StalwartManagementTestHost.DomainLookup([("d7", "acme.example")], extra: ",\"catchAllAddress\":\"alice@acme.example\""));
        StubDkim("k1");

        (await Domains().FindAsync(Acme, Ct)).Value.CatchAllAddress.ShouldBe("alice@acme.example");
        _host.Calls("x:Domain/get").Single()[1]!["properties"]!.AsArray().Select(p => p!.GetValue<string>()).ShouldContain("catchAllAddress");
    }

    [Fact]
    public async Task Sets_and_clears_a_domains_catch_all_without_a_reload()
    {
        _host.Stub("x:Domain/query", StalwartManagementTestHost.DomainLookup(("d7", "acme.example")));
        _host.Stub("x:Domain/set", """[["x:Domain/set",{"updated":{"d7":null}},"c0"]]""");
        MailAddress alice = MailAddress.TryParse("alice@acme.example", out MailAddress? a) ? a : throw new ArgumentException("alice");

        (await Domains().SetCatchAllAsync(Acme, alice, Ct)).IsSuccess.ShouldBeTrue();
        (await Domains().SetCatchAllAsync(Acme, null, Ct)).IsSuccess.ShouldBeTrue();

        var sets = _host.Calls("x:Domain/set");
        sets[0][1]!["update"]!["d7"]!["catchAllAddress"]!.GetValue<string>().ShouldBe("alice@acme.example");
        sets[1][1]!["update"]!["d7"]!.AsObject().ContainsKey("catchAllAddress").ShouldBeTrue();
        sets[1][1]!["update"]!["d7"]!["catchAllAddress"].ShouldBeNull();
        _host.Calls("x:Action/set").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_catch_all_for_a_domain_the_mail_server_lacks_or_refuses_is_reported()
    {
        _host.Stub("x:Domain/query", StalwartManagementTestHost.DomainLookup());

        (await Domains().SetCatchAllAsync(Acme, null, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _host.Calls("x:Domain/set").ShouldBeEmpty();

        _host.Stub("x:Domain/query", StalwartManagementTestHost.DomainLookup(("d7", "acme.example")));
        _host.Stub("x:Domain/set", """[["x:Domain/set",{"notUpdated":{"d7":{"type":"forbidden","description":"no"}}},"c0"]]""");

        (await Domains().SetCatchAllAsync(Acme, null, Ct)).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public async Task Only_an_exact_name_matches()
    {
        _host.Stub("x:Domain/query", StalwartManagementTestHost.DomainLookup(("d8", "mail.acme.example"), ("d9", "acme.example.org")));

        var found = await Domains().FindAsync(Acme, Ct);

        found.Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _host.Calls("x:DkimSignature/query").ShouldBeEmpty();
    }

    [Fact]
    public async Task Matching_ignores_case_and_a_trailing_dot()
    {
        _host.Stub("x:Domain/query", StalwartManagementTestHost.DomainLookup(("d8", "mail.acme.example"), ("d7", "ACME.example.")));
        StubDkim();

        (await Domains().FindAsync(Acme, Ct)).Value.Id.ShouldBe("d7");
    }

    [Fact]
    public async Task An_unknown_domain_is_not_found()
    {
        _host.Stub("x:Domain/query", StalwartManagementTestHost.DomainLookup());

        (await Domains().FindAsync(Acme, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task A_failed_dkim_query_is_reported()
    {
        _host.Stub("x:Domain/query", StalwartManagementTestHost.DomainLookup(("d7", "acme.example")));
        _host.Stub("x:DkimSignature/query", """[["error",{"type":"forbidden"},"c0"]]""");

        (await Domains().FindAsync(Acme, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task Creates_with_automatic_dkim_and_manual_dns_then_reloads_in_the_same_request()
    {
        _host.Stub("x:Domain/set", """[["x:Domain/set",{"created":{"d":{"id":"d9"}}},"c0"],["x:Action/set",{"created":{"r":{"id":"a1"}}},"c1"]]""");
        StubDkim("k1");

        var info = (await Domains().CreateAsync(Acme, "Acme (acme)", Ct)).Value;

        info.ShouldBe(new MailDomainInfo("d9", Acme, DkimPresent: true));
        var body = _host.Bodies()[0];
        var set = body["methodCalls"]![0]!.AsArray();
        set[0]!.GetValue<string>().ShouldBe("x:Domain/set");
        var create = set[1]!["create"]!["d"]!;
        create["name"]!.GetValue<string>().ShouldBe("acme.example");
        create["description"]!.GetValue<string>().ShouldBe("Acme (acme)");
        create["dkimManagement"]!["@type"]!.GetValue<string>().ShouldBe("Automatic");
        create["dnsManagement"]!["@type"]!.GetValue<string>().ShouldBe("Manual");
        var reload = body["methodCalls"]![1]!.AsArray();
        reload[0]!.GetValue<string>().ShouldBe("x:Action/set");
        reload[1]!["create"]!["r"]!["@type"]!.GetValue<string>().ShouldBe("ReloadSettings");
        _host.Calls("x:DkimSignature/query").Single()[1]!["filter"]!["domainId"]!.GetValue<string>().ShouldBe("d9");
    }

    [Theory]
    [InlineData("alreadyExists", ErrorKind.Conflict)]
    [InlineData("forbidden", ErrorKind.Forbidden)]
    [InlineData("invalidProperties", ErrorKind.Validation)]
    public async Task Maps_a_refused_create(string type, ErrorKind kind)
    {
        _host.Stub("x:Domain/set", $$$$"""[["x:Domain/set",{"notCreated":{"d":{"type":"{{{{type}}}}","description":"no"}}},"c0"],["x:Action/set",{"created":{"r":{"id":"a1"}}},"c1"]]""");

        (await Domains().CreateAsync(Acme, "Acme", Ct)).Error!.Kind.ShouldBe(kind);
        _host.Calls("x:DkimSignature/query").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failed_reload_after_a_create_is_reported()
    {
        _host.Stub("x:Domain/set", """[["x:Domain/set",{"created":{"d":{"id":"d9"}}},"c0"],["x:Action/set",{"notCreated":{"r":{"type":"forbidden"}}},"c1"]]""");

        var created = await Domains().CreateAsync(Acme, "Acme", Ct);

        created.Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        created.Error.Message.ShouldContain("did not reload");
    }

    [Fact]
    public async Task Reloads_settings_on_its_own()
    {
        _host.Stub("x:Action/set", """[["x:Action/set",{"created":{"r":{"id":"a1"}}},"c0"]]""");

        (await Domains().ReloadSettingsAsync(Ct)).IsSuccess.ShouldBeTrue();

        var reload = _host.Calls("x:Action/set").Single();
        reload[1]!["create"]!["r"]!["@type"]!.GetValue<string>().ShouldBe("ReloadSettings");
    }

    [Fact]
    public async Task A_refused_reload_is_reported_and_logged()
    {
        _host.Stub("x:Action/set", """[["x:Action/set",{"notCreated":{"r":{"type":"forbidden","description":"Insufficient permissions"}}},"c0"]]""");

        (await Domains().ReloadSettingsAsync(Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        _host.Logs.AllText.ShouldContain("ReloadSettings failed (Forbidden)");
    }

    [Fact]
    public async Task Reads_the_dns_records_from_the_zone_file()
    {
        const string zone = "$ORIGIN acme.example.\\n@ 3600 IN MX 10 mail.acme.example.\\n_dmarc IN TXT \\\"v=DMARC1; p=reject\\\"\\n";
        _host.Stub("x:Domain/query", StalwartManagementTestHost.DomainLookup([("d7", "acme.example")], $",\"dnsZoneFile\":\"{zone}\""));

        var records = (await Domains().GetDnsRecordsAsync(Acme, Ct)).Value;

        records.ShouldBe([
            new DnsRecord("acme.example", "MX", 3600, "10 mail.acme.example"),
            new DnsRecord("_dmarc.acme.example", "TXT", 3600, "v=DMARC1; p=reject"),
        ]);
        _host.Calls("x:Domain/get").Single()[1]!["properties"]!.AsArray().Select(p => p!.GetValue<string>()).ShouldBe(["name", "dnsZoneFile"]);
    }

    [Fact]
    public async Task A_domain_without_zone_data_has_no_records()
    {
        _host.Stub("x:Domain/query", StalwartManagementTestHost.DomainLookup(("d7", "acme.example")));

        (await Domains().GetDnsRecordsAsync(Acme, Ct)).Value.ShouldBeEmpty();
    }

    [Fact]
    public async Task Unreadable_zone_lines_are_logged()
    {
        _host.Stub("x:Domain/query", StalwartManagementTestHost.DomainLookup([("d7", "acme.example")], ",\"dnsZoneFile\":\"$INCLUDE other.zone\\n\""));

        (await Domains().GetDnsRecordsAsync(Acme, Ct)).Value.ShouldBeEmpty();
        _host.Logs.AllText.ShouldContain("Skipped 1 unreadable line");
    }

    [Fact]
    public async Task Dns_records_of_an_unknown_domain_are_not_found()
    {
        _host.Stub("x:Domain/query", StalwartManagementTestHost.DomainLookup());

        (await Domains().GetDnsRecordsAsync(Acme, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task A_method_error_on_lookup_is_returned()
    {
        _host.Stub("x:Domain/query", """[["error",{"type":"forbidden"},"c0"],["error",{"type":"invalidResultReference"},"c1"]]""");

        (await Domains().FindAsync(Acme, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    // Shapes as Stalwart 1.1.0 answers (throwaway container, 2026-11-18).
    private const string LinkedToKeys = """{"notDestroyed":{"d7":{"type":"objectIsLinked","objectId":{"object":"Domain","id":"d7"},"linkedObjects":[{"object":"DkimSignature","id":"k1"},{"object":"DkimSignature","id":"k2"}]}}}""";

    private const string Reloaded = """["x:Action/set",{"created":{"r":{"id":"a1"}}},"c1"]""";

    [Fact]
    public async Task Destroying_a_domain_with_only_its_dkim_keys_takes_the_keys_then_the_domain_then_reloads()
    {
        _host.Stub("x:Domain/query", StalwartManagementTestHost.DomainLookup(("d7", "acme.example")));
        _host.Stub("x:Domain/set", $$$$$"""[["x:Domain/set",{{{{{LinkedToKeys}}}}},"c0"],{{{{{Reloaded}}}}}]""");
        _host.Stub("x:DkimSignature/set", """[["x:DkimSignature/set",{"destroyed":["k1","k2"]},"c0"],["x:Domain/set",{"destroyed":["d7"]},"c1"],["x:Action/set",{"created":{"r":{"id":"a2"}}},"c2"]]""");

        (await Domains().DestroyAsync(Acme, Ct)).IsSuccess.ShouldBeTrue();

        var bodies = _host.Bodies();
        var first = bodies[1]["methodCalls"]!.AsArray();
        first[0]![0]!.GetValue<string>().ShouldBe("x:Domain/set");
        first[0]![1]!["destroy"]![0]!.GetValue<string>().ShouldBe("d7");
        first[1]![1]!["create"]!["r"]!["@type"]!.GetValue<string>().ShouldBe("ReloadSettings");
        var second = bodies[2]["methodCalls"]!.AsArray();
        second.Select(c => c![0]!.GetValue<string>()).ShouldBe(["x:DkimSignature/set", "x:Domain/set", "x:Action/set"]);
        second[0]![1]!["destroy"]!.AsArray().Select(i => i!.GetValue<string>()).ShouldBe(["k1", "k2"]);
        second[1]![1]!["destroy"]![0]!.GetValue<string>().ShouldBe("d7");
        _host.Logs.AllText.ShouldContain("Destroyed Stalwart domain acme.example (d7)");
    }

    [Fact]
    public async Task A_domain_with_nothing_linked_is_destroyed_in_one_request()
    {
        _host.Stub("x:Domain/query", StalwartManagementTestHost.DomainLookup(("d7", "acme.example")));
        _host.Stub("x:Domain/set", $$$$$"""[["x:Domain/set",{"destroyed":["d7"]},"c0"],{{{{{Reloaded}}}}}]""");

        (await Domains().DestroyAsync(Acme, Ct)).IsSuccess.ShouldBeTrue();
        _host.Calls("x:DkimSignature/set").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_domain_an_account_still_uses_is_a_conflict_and_keeps_its_dkim_keys()
    {
        _host.Stub("x:Domain/query", StalwartManagementTestHost.DomainLookup(("d7", "acme.example")));
        _host.Stub("x:Domain/set", $$$$$"""[["x:Domain/set",{"notDestroyed":{"d7":{"type":"objectIsLinked","linkedObjects":[{"object":"Account","id":"c"},{"object":"DkimSignature","id":"k1"}]}}},"c0"],{{{{{Reloaded}}}}}]""");

        var destroyed = await Domains().DestroyAsync(Acme, Ct);

        destroyed.Error!.Kind.ShouldBe(ErrorKind.Conflict);
        destroyed.Error.Message.ShouldContain("1 account(s) with an address or alias on acme.example");
        _host.Calls("x:DkimSignature/set").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_domain_a_list_still_uses_is_a_conflict_naming_lists()
    {
        _host.Stub("x:Domain/query", StalwartManagementTestHost.DomainLookup(("d7", "acme.example")));
        _host.Stub("x:Domain/set", $$$$$"""[["x:Domain/set",{"notDestroyed":{"d7":{"type":"objectIsLinked","linkedObjects":[{"object":"Account","id":"c"},{"object":"MailingList","id":"b"},{"object":"DkimSignature","id":"k1"}]}}},"c0"],{{{{{Reloaded}}}}}]""");

        var destroyed = await Domains().DestroyAsync(Acme, Ct);

        destroyed.Error!.Message.ShouldContain("1 account(s) and 1 list(s) with an address or alias on acme.example");
        _host.Calls("x:DkimSignature/set").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_domain_still_linked_after_its_keys_went_is_refused_without_a_third_try()
    {
        _host.Stub("x:Domain/query", StalwartManagementTestHost.DomainLookup(("d7", "acme.example")));
        _host.Stub("x:Domain/set", $$$$$"""[["x:Domain/set",{{{{{LinkedToKeys}}}}},"c0"],{{{{{Reloaded}}}}}]""");
        _host.Stub("x:DkimSignature/set", """[["x:DkimSignature/set",{"destroyed":["k1","k2"]},"c0"],["x:Domain/set",{"notDestroyed":{"d7":{"type":"objectIsLinked","linkedObjects":[{"object":"Account","id":"c"}]}}},"c1"],["x:Action/set",{"created":{"r":{"id":"a2"}}},"c2"]]""");

        (await Domains().DestroyAsync(Acme, Ct)).IsSuccess.ShouldBeFalse();
        _host.Calls("x:Domain/set").Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_refused_dkim_key_destroy_is_reported_as_the_cause()
    {
        _host.Stub("x:Domain/query", StalwartManagementTestHost.DomainLookup(("d7", "acme.example")));
        _host.Stub("x:Domain/set", $$$$$"""[["x:Domain/set",{{{{{LinkedToKeys}}}}},"c0"],{{{{{Reloaded}}}}}]""");
        _host.Stub("x:DkimSignature/set", """[["x:DkimSignature/set",{"notDestroyed":{"k1":{"type":"forbidden"}},"destroyed":["k2"]},"c0"],["x:Domain/set",{"notDestroyed":{"d7":{"type":"objectIsLinked"}}},"c1"],["x:Action/set",{"created":{"r":{"id":"a2"}}},"c2"]]""");

        (await Domains().DestroyAsync(Acme, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task A_link_stalwart_does_not_name_is_a_conflict_without_a_second_request()
    {
        _host.Stub("x:Domain/query", StalwartManagementTestHost.DomainLookup(("d7", "acme.example")));
        _host.Stub("x:Domain/set", $$$$$"""[["x:Domain/set",{"notDestroyed":{"d7":{"type":"objectIsLinked"}}},"c0"],{{{{{Reloaded}}}}}]""");

        (await Domains().DestroyAsync(Acme, Ct)).Error!.Kind.ShouldBe(ErrorKind.Conflict);
        _host.Calls("x:Domain/set").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Destroying_an_unknown_domain_is_not_found()
    {
        _host.Stub("x:Domain/query", StalwartManagementTestHost.DomainLookup());

        (await Domains().DestroyAsync(Acme, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
        _host.Calls("x:Domain/set").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_forbidden_destroy_is_reported()
    {
        _host.Stub("x:Domain/query", StalwartManagementTestHost.DomainLookup(("d7", "acme.example")));
        _host.Stub("x:Domain/set", $$$$$"""[["x:Domain/set",{"notDestroyed":{"d7":{"type":"forbidden"}}},"c0"],{{{{{Reloaded}}}}}]""");

        (await Domains().DestroyAsync(Acme, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task A_failed_reload_after_a_destroy_is_reported()
    {
        _host.Stub("x:Domain/query", StalwartManagementTestHost.DomainLookup(("d7", "acme.example")));
        _host.Stub("x:Domain/set", """[["x:Domain/set",{"destroyed":["d7"]},"c0"],["x:Action/set",{"notCreated":{"r":{"type":"forbidden"}}},"c1"]]""");

        var destroyed = await Domains().DestroyAsync(Acme, Ct);

        destroyed.Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        destroyed.Error.Message.ShouldContain("treats the domain as its own until it does");
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();
}
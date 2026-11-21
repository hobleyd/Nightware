using System.Text;
using Nightware.Adapters.Jmap.Mail.Rules;
using Nightware.Adapters.Jmap.MailboxSettings;
using Nightware.Adapters.Jmap.Tests.Mail.Write.Support;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;
using Nightware.Domain.MailboxSettings;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Nightware.Adapters.Jmap.Tests.Mail.Rules;

/// <summary><see cref="JmapMessageRuleStore"/>, <see cref="NightwareSieve"/> and the script path of <see cref="JmapAutoReplyStore"/>.</summary>
public sealed class JmapMessageRuleStoreTests : IAsyncLifetime
{
    private static readonly MailboxRef Alice = new("alice@nightware.localhost");
    private static readonly MessageRule Filing = new("r1", "Filing", 1, true)
    {
        Conditions = new MessageRulePredicates { SubjectContains = ["invoice"] },
        Actions = new MessageRuleActions { MoveToFolder = new FolderId("a"), MarkAsRead = true },
    };

    private readonly FakeJmapServer _jmap = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _jmap.OnFixture("Mailbox/get", "mailbox-get.json");
        _jmap.On("SieveScript/validate", """{"accountId":"c","error":null}""");
        _jmap.StubUpload("U1", "application/sieve", 100);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _jmap.DisposeAsync();

    private NightwareSieve Sieve() => new(_jmap.Client(), _jmap.Blobs());

    private JmapMessageRuleStore Store() => new(_jmap.Client(), Sieve());

    private JmapAutoReplyStore Replies() => new(_jmap.Client(), Sieve());

    private void NoScript() => _jmap.On("SieveScript/get", """{"accountId":"c","state":"1","list":[],"notFound":[]}""");

    private void Script(FilterState state, bool active = true)
    {
        _jmap.On("SieveScript/get", $$"""{"accountId":"c","state":"1","list":[{"id":"s9","name":"nightware-rules","blobId":"B1","isActive":{{(active ? "true" : "false")}}}],"notFound":[]}""");
        _jmap.Host.Server.Given(Request.Create().WithPath("/jmap/download/c/B1/nightware-rules").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/sieve").WithBody(SieveScriptWriter.Write(state, null)));
    }

    private void Vacation(string fields) =>
        _jmap.On("VacationResponse/get", $$"""{"accountId":"c","state":"1","list":[{"id":"singleton",{{fields}}}],"notFound":[]}""");

    private string Uploaded() => Encoding.UTF8.GetString(_jmap.UploadedBody());

    [Fact]
    public async Task A_mailbox_without_the_script_has_no_rules()
    {
        NoScript();

        (await Store().ListAsync(Alice, Ct)).Value.ShouldBeEmpty();
    }

    [Fact]
    public async Task Rules_are_read_from_the_script_s_state()
    {
        Script(new FilterState([Filing], null));

        (await Store().ListAsync(Alice, Ct)).Value.Single().Conditions.SubjectContains.ShouldBe(["invoice"]);
    }

    [Fact]
    public async Task The_first_save_creates_and_activates_the_script_and_takes_over_an_enabled_vacation()
    {
        NoScript();
        Vacation("\"isEnabled\":true,\"htmlBody\":\"<p>Away</p>\"");
        _jmap.On("SieveScript/set", """{"accountId":"c","oldState":"1","newState":"2","created":{"script":{"id":"s9","blobId":"U1","isActive":true}}}""");

        (await Store().SaveAsync(Alice, [Filing], Ct)).IsSuccess.ShouldBeTrue();

        var set = _jmap.Single("SieveScript/set");
        set["create"]!["script"]!["name"]!.GetValue<string>().ShouldBe("nightware-rules");
        set["create"]!["script"]!["blobId"]!.GetValue<string>().ShouldBe("U1");
        set["onSuccessActivateScript"]!.GetValue<string>().ShouldBe("#script");
        _jmap.Single("SieveScript/validate")["blobId"]!.GetValue<string>().ShouldBe("U1");
        Uploaded().ShouldContain("vacation :mime");
        Uploaded().ShouldContain("header :contains \"subject\" [\"invoice\"]");
    }

    [Fact]
    public async Task Later_saves_update_the_script_and_keep_its_reply()
    {
        Script(new FilterState([], new AutoReplySetting(AutoReplyStatus.AlwaysEnabled) { ExternalReplyMessage = "kept" }));
        _jmap.On("SieveScript/set", """{"accountId":"c","oldState":"1","newState":"2","updated":{"s9":null}}""");

        (await Store().SaveAsync(Alice, [Filing], Ct)).IsSuccess.ShouldBeTrue();

        var set = _jmap.Single("SieveScript/set");
        set["update"]!["s9"]!["blobId"]!.GetValue<string>().ShouldBe("U1");
        set["onSuccessActivateScript"]!.GetValue<string>().ShouldBe("s9");
        Uploaded().ShouldContain("kept");
        _jmap.Calls().ShouldNotContain(c => c.Method == "VacationResponse/get");
    }

    [Fact]
    public async Task The_user_s_own_active_filter_is_never_switched_off()
    {
        _jmap.On("SieveScript/get", """{"accountId":"c","state":"1","list":[{"id":"u1","name":"my-filter","blobId":"X","isActive":true}],"notFound":[]}""");
        Vacation("\"isEnabled\":false");

        var result = await Store().SaveAsync(Alice, [Filing], Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Conflict);
        result.Error.Message.ShouldContain("my-filter");
        _jmap.Calls().ShouldNotContain(c => c.Method == "SieveScript/set");
    }

    [Fact]
    public async Task A_rule_naming_an_unknown_folder_is_refused_before_anything_is_written()
    {
        NoScript();
        Vacation("\"isEnabled\":false");

        var result = await Store().SaveAsync(Alice, [Filing with { Actions = new MessageRuleActions { MoveToFolder = new FolderId("nope") } }], Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        _jmap.Calls().ShouldNotContain(c => c.Method == "SieveScript/set");
    }

    [Fact]
    public async Task A_script_that_does_not_compile_changes_nothing()
    {
        NoScript();
        Vacation("\"isEnabled\":false");
        _jmap.On("SieveScript/validate", """{"accountId":"c","error":{"type":"invalidSieve","description":"line 4"}}""");

        (await Store().SaveAsync(Alice, [Filing], Ct)).Error!.Kind.ShouldBe(ErrorKind.Unknown);
        _jmap.Calls().ShouldNotContain(c => c.Method == "SieveScript/set");
    }

    [Fact]
    public async Task While_the_script_is_active_automatic_replies_are_read_and_written_there()
    {
        Script(new FilterState([Filing], new AutoReplySetting(AutoReplyStatus.AlwaysEnabled) { ExternalReplyMessage = "<p>Out</p>" }));
        _jmap.On("SieveScript/set", """{"accountId":"c","oldState":"1","newState":"2","updated":{"s9":null}}""");

        var read = (await Replies().GetAsync(Alice, Ct)).Value;
        read.Status.ShouldBe(AutoReplyStatus.AlwaysEnabled);
        read.InternalReplyMessage.ShouldBe("<p>Out</p>");

        var written = (await Replies().SetAsync(Alice, new AutoReplySetting(AutoReplyStatus.Disabled), ["example.com"], Ct)).Value;
        written.Status.ShouldBe(AutoReplyStatus.Disabled);
        Uploaded().ShouldNotContain("vacation :mime");
        Uploaded().ShouldContain("invoice");
        _jmap.Calls().ShouldNotContain(c => c.Method == "VacationResponse/get" || c.Method == "VacationResponse/set");
    }

    [Fact]
    public async Task When_vacation_took_over_elsewhere_its_reply_is_what_is_reported()
    {
        Script(new FilterState([Filing], null), active: false);
        Vacation("\"isEnabled\":true,\"htmlBody\":\"<p>Set in webmail</p>\"");

        var read = (await Replies().GetAsync(Alice, Ct)).Value;

        read.Status.ShouldBe(AutoReplyStatus.AlwaysEnabled);
        read.ExternalReplyMessage.ShouldBe("<p>Set in webmail</p>");
    }

    [Fact]
    public async Task A_reply_with_separate_internal_and_external_messages_creates_the_script()
    {
        NoScript();
        _jmap.On("SieveScript/set", """{"accountId":"c","oldState":"1","newState":"2","created":{"script":{"id":"s9","blobId":"U1","isActive":true}}}""");
        var setting = new AutoReplySetting(AutoReplyStatus.AlwaysEnabled)
        {
            ExternalAudience = ExternalAudience.All,
            InternalReplyMessage = "<p>Back Monday, ping me on chat</p>",
            ExternalReplyMessage = "<p>Out of the office</p>",
        };

        var written = (await Replies().SetAsync(Alice, setting, ["acme.example", "nightware.localhost"], Ct)).Value;

        (written.InternalReplyMessage, written.ExternalReplyMessage, written.ExternalAudience).ShouldBe(("<p>Back Monday, ping me on chat</p>", "<p>Out of the office</p>", ExternalAudience.All));
        Uploaded().ShouldContain("if envelope :domain :is \"from\" [\"acme.example\", \"nightware.localhost\"] {");
        Uploaded().ShouldContain("ping me on chat");
        Uploaded().ShouldContain("} else {");
        _jmap.Calls().ShouldNotContain(c => c.Method == "VacationResponse/set");
    }

    [Fact]
    public async Task The_split_reply_reads_back_with_both_messages_and_survives_a_rule_save()
    {
        var setting = new AutoReplySetting(AutoReplyStatus.AlwaysEnabled) { ExternalAudience = ExternalAudience.None, InternalReplyMessage = "<p>in</p>", ExternalReplyMessage = "<p>out</p>" };
        Script(new FilterState([], setting) { InternalDomains = ["acme.example"] });
        _jmap.On("SieveScript/set", """{"accountId":"c","oldState":"1","newState":"2","updated":{"s9":null}}""");

        var read = (await Replies().GetAsync(Alice, Ct)).Value;
        (read.ExternalAudience, read.InternalReplyMessage, read.ExternalReplyMessage).ShouldBe((ExternalAudience.None, "<p>in</p>", "<p>out</p>"));

        (await Store().SaveAsync(Alice, [Filing], Ct)).IsSuccess.ShouldBeTrue();
        Uploaded().ShouldContain("if envelope :domain :is \"from\" [\"acme.example\"] {");
        Uploaded().ShouldNotContain("} else {", Case.Sensitive, "audience none: nobody outside gets a reply");
    }

    [Fact]
    public async Task A_plain_reply_to_everyone_without_the_script_stays_a_vacation_response()
    {
        NoScript();
        _jmap.On("VacationResponse/set", """{"accountId":"c","oldState":"1","newState":"2","updated":{"singleton":null}}""");
        Vacation("\"isEnabled\":true,\"htmlBody\":\"<p>Away</p>\"");

        await Replies().SetAsync(Alice, new AutoReplySetting(AutoReplyStatus.AlwaysEnabled) { InternalReplyMessage = "<p>Away</p>", ExternalReplyMessage = "<p>Away</p>" }, ["acme.example"], Ct);

        _jmap.Calls().ShouldNotContain(c => c.Method == "SieveScript/set");
        _jmap.Calls().ShouldContain(c => c.Method == "VacationResponse/set");
    }
}

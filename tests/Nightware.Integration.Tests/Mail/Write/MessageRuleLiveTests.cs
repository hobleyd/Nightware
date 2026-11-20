using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Nightware.Integration.Tests.Directory;
using Nightware.Integration.Tests.Support;

namespace Nightware.Integration.Tests.Mail.Write;

/// <summary>
/// Phase 12 Inbox rules against the running stack (ADR 0006 D5): the rule becomes Nightware's Sieve script, a
/// delivery is filed by it, and automatic replies written while rules exist live in the same script.
/// Afterwards the script is removed, leaving alice's account as it was.
/// </summary>
public sealed class MessageRuleLiveTests : IAsyncLifetime
{
    private const string Alice = "alice";
    private const string Sieve = "urn:ietf:params:jmap:sieve";
    private static readonly string AliceAddress = DevStack.Address("alice");

    private LiveStack _stack = null!;
    private StalwartProbe _probe = null!;
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _stack = LiveStack.Start();
        _probe = new StalwartProbe(_stack);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_stack is not null)
        {
            await _stack.DisposeAsync();
        }
    }

    private static async Task<(HttpStatusCode Status, JsonObject? Body)> SendAsync(HttpClient client, HttpMethod method, string path, string? json = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text)!.AsObject());
    }

    private async Task RemoveScriptAsync()
    {
        var scripts = await _probe.CallAsync(Alice, """[["SieveScript/get",{"accountId":"$account"},"g"]]""", Ct, Sieve);
        var ours = scripts[0]![1]!["list"]!.AsArray().Where(s => s!["name"]!.GetValue<string>() == "nightware-rules").Select(s => s!["id"]!.GetValue<string>()).ToList();
        if (ours.Count > 0)
        {
            await _probe.CallAsync(Alice, $$"""[["SieveScript/set",{"accountId":"$account","onSuccessDeactivateScript":true},"d"],["SieveScript/set",{"accountId":"$account","destroy":["{{ours[0]}}"]},"x"]]""", Ct, Sieve);
        }
    }

    [Fact]
    public async Task A_rule_files_a_delivery_and_auto_replies_written_meanwhile_share_its_script()
    {
        var alice = await _stack.GraphClientAsync(Alice, Ct);
        var (_, folder) = await SendAsync(alice, HttpMethod.Post, "/v1.0/me/mailFolders/msgfolderroot/childFolders", $$"""{"displayName":"nw-rule-{{_run}}"}""");
        var folderId = folder!["id"]!.GetValue<string>();
        string? ruleId = null;
        try
        {
            var (created, rule) = await SendAsync(alice, HttpMethod.Post, "/v1.0/me/mailFolders/inbox/messageRules", $$$"""
                {"displayName":"File {{{_run}}} \"quoted\"","sequence":5,"isEnabled":true,
                 "conditions":{"subjectContains":["nw-rule-{{{_run}}}"]},"exceptions":{"hasAttachments":true},
                 "actions":{"moveToFolder":"{{{folderId}}}","markAsRead":true,"stopProcessingRules":true}}
                """);
            created.ShouldBe(HttpStatusCode.Created, rule?.ToJsonString());
            ruleId = rule!["id"]!.GetValue<string>();
            rule["sequence"]!.GetValue<int>().ShouldBe(1);

            (await SendAsync(alice, HttpMethod.Post, "/v1.0/me/mailFolders/inbox/messageRules", """{"displayName":"x","actions":{"assignCategories":["Red"]}}""")).Status
                .ShouldBe(HttpStatusCode.NotImplemented);

            var subject = $"Filed by nw-rule-{_run}";
            await SendAsync(alice, HttpMethod.Post, "/v1.0/me/sendMail", $$$"""
                {"message":{"subject":"{{{subject}}}","body":{"contentType":"text","content":"rule"},
                  "toRecipients":[{"emailAddress":{"address":"{{{AliceAddress}}}"}}]},"saveToSentItems":false}
                """);
            var filed = await WaitInFolderAsync(alice, folderId, subject);
            filed["isRead"]!.GetValue<bool>().ShouldBeTrue();

            // Auto-replies written now go into the same script, which stays the active one.
            var (setStatus, settings) = await SendAsync(alice, HttpMethod.Patch, "/v1.0/me/mailboxSettings",
                """{"automaticRepliesSetting":{"status":"alwaysEnabled","externalReplyMessage":"<p>Away – back soon</p>"}}""");
            setStatus.ShouldBe(HttpStatusCode.OK, settings?.ToJsonString());
            var (_, read) = await SendAsync(alice, HttpMethod.Get, "/v1.0/me/mailboxSettings?$select=automaticRepliesSetting");
            read!["automaticRepliesSetting"]!["status"]!.GetValue<string>().ShouldBe("alwaysEnabled");
            read["automaticRepliesSetting"]!["externalReplyMessage"]!.GetValue<string>().ShouldBe("<p>Away – back soon</p>");
            var scripts = await _probe.CallAsync(Alice, """[["SieveScript/get",{"accountId":"$account"},"g"]]""", Ct, Sieve);
            scripts[0]![1]!["list"]!.AsArray().Single(x => x!["isActive"]!.GetValue<bool>())!["name"]!.GetValue<string>().ShouldBe("nightware-rules");
            (await SendAsync(alice, HttpMethod.Get, "/v1.0/me/mailFolders/inbox/messageRules")).Body!["value"]!.AsArray().Count.ShouldBe(1);

            await SendAsync(alice, HttpMethod.Patch, "/v1.0/me/mailboxSettings", """{"automaticRepliesSetting":{"status":"disabled"}}""");
            var (_, off) = await SendAsync(alice, HttpMethod.Get, "/v1.0/me/mailboxSettings?$select=automaticRepliesSetting");
            off!["automaticRepliesSetting"]!["status"]!.GetValue<string>().ShouldBe("disabled");
        }
        finally
        {
            if (ruleId is not null)
            {
                (await SendAsync(alice, HttpMethod.Delete, $"/v1.0/me/mailFolders/inbox/messageRules/{ruleId}")).Status.ShouldBe(HttpStatusCode.NoContent);
            }

            await SendAsync(alice, HttpMethod.Delete, $"/v1.0/me/mailFolders/{folderId}");
            await RemoveScriptAsync();
        }
    }

    private static async Task<JsonObject> WaitInFolderAsync(HttpClient alice, string folderId, string subject)
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            var (_, page) = await SendAsync(alice, HttpMethod.Get, $"/v1.0/me/mailFolders/{folderId}/messages?$select=subject,isRead&$top=10");
            if (page!["value"]!.AsArray().FirstOrDefault(m => m!["subject"]!.GetValue<string>() == subject) is JsonObject found)
            {
                return found;
            }

            await Task.Delay(1000, Ct);
        }

        throw new ShouldAssertException($"'{subject}' never reached the rule's folder.");
    }
}

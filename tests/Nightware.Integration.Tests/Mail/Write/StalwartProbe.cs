using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nightware.Integration.Tests.Directory;
using Nightware.Integration.Tests.Support;

namespace Nightware.Integration.Tests.Mail.Write;

/// <summary>
/// Looks at Stalwart directly over JMAP, as the test user, to check what the façade wrote: the mail
/// tests must not depend on the read side to verify the write side.
/// </summary>
internal sealed class StalwartProbe(LiveStack stack)
{
    private static string MailUrl => DevStack.MailBase;
    private readonly Dictionary<string, (string Token, string Account)> _sessions = [];

    /// <summary>Runs <paramref name="methodCalls"/> (a JSON array literal; <c>$account</c> is replaced) and returns <c>methodResponses</c>.</summary>
    /// <param name="capabilities">Further capabilities for <c>using</c> (e.g. <c>urn:ietf:params:jmap:sieve</c>).</param>
    public async Task<JsonArray> CallAsync(string user, string methodCalls, CancellationToken cancellationToken, params string[] capabilities)
    {
        var (token, account) = await SessionAsync(user, cancellationToken);
        string[] usingList = ["urn:ietf:params:jmap:core", "urn:ietf:params:jmap:mail", "urn:ietf:params:jmap:submission", .. capabilities];
        var body = """{"using":""" + new JsonArray([.. usingList.Select(static c => (JsonNode)c)]).ToJsonString() + ""","methodCalls":"""
            + methodCalls.Replace("$account", account, StringComparison.Ordinal) + "}";
        using var request = new HttpRequestMessage(HttpMethod.Post, MailUrl + "/jmap/") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await stack.Backchannel.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken))!["methodResponses"]!.AsArray();
    }

    /// <summary>A blob's bytes as text (e.g. a Sieve script), through the session's download URL.</summary>
    public async Task<string> DownloadTextAsync(string user, string blobId, CancellationToken cancellationToken)
    {
        var (token, account) = await SessionAsync(user, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{MailUrl}/jmap/download/{Uri.EscapeDataString(account)}/{Uri.EscapeDataString(blobId)}/blob?accept=text/plain");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await stack.Backchannel.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    /// <summary>The newest messages of a role mailbox (inbox, sent, drafts …), with the given properties.</summary>
    public async Task<IReadOnlyList<JsonObject>> RecentAsync(string user, string role, string properties, CancellationToken cancellationToken)
    {
        var mailbox = (await MailboxesAsync(user, cancellationToken)).Single(m => m["role"]?.GetValue<string>() == role)["id"]!.GetValue<string>();
        var responses = await CallAsync(user, $$$"""
            [["Email/query",{"accountId":"$account","filter":{"inMailbox":"{{{mailbox}}}"},"sort":[{"property":"receivedAt","isAscending":false}],"limit":30},"q"],
             ["Email/get",{"accountId":"$account","#ids":{"resultOf":"q","name":"Email/query","path":"/ids"},"properties":{{{properties}}}},"g"]]
            """, cancellationToken);
        return [.. responses[^1]![1]!["list"]!.AsArray().Select(static e => e!.AsObject())];
    }

    /// <summary>Waits (up to 30 s) for a message with <paramref name="subject"/> to arrive in a role mailbox.</summary>
    public async Task<JsonObject> WaitForAsync(string user, string role, string subject, CancellationToken cancellationToken, string properties = """["id","subject","threadId","mailboxIds","keywords","from","to","cc","bcc","receivedAt","sentAt","messageId","inReplyTo","references","attachments","hasAttachment"]""")
    {
        for (var attempt = 0; attempt < 30; attempt++)
        {
            if ((await RecentAsync(user, role, properties, cancellationToken)).FirstOrDefault(e => e["subject"]?.GetValue<string>() == subject) is { } found)
            {
                return found;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        throw new ShouldAssertException($"'{subject}' never reached {user}'s {role}.");
    }

    /// <summary>
    /// One message by JMAP id. Use this rather than <see cref="WaitForAsync"/> for backdated mail
    /// (imports): the shared test inbox keeps growing, so a message received "in the past" soon falls
    /// out of the newest-first window that WaitForAsync scans.
    /// </summary>
    public async Task<JsonObject> GetAsync(string user, string jmapId, CancellationToken cancellationToken, string properties = """["id","subject","mailboxIds","keywords","from","receivedAt","sentAt"]""")
    {
        var responses = await CallAsync(user, $$$"""[["Email/get",{"accountId":"$account","ids":["{{{jmapId}}}"],"properties":{{{properties}}}},"g"]]""", cancellationToken);
        return responses[0]![1]!["list"]!.AsArray().ShouldHaveSingleItem()!.AsObject();
    }

    public async Task<IReadOnlyList<JsonObject>> MailboxesAsync(string user, CancellationToken cancellationToken)
    {
        var responses = await CallAsync(user, """[["Mailbox/get",{"accountId":"$account","properties":["id","name","parentId","role"]},"m"]]""", cancellationToken);
        return [.. responses[0]![1]!["list"]!.AsArray().Select(static m => m!.AsObject())];
    }

    private async Task<(string Token, string Account)> SessionAsync(string user, CancellationToken cancellationToken)
    {
        if (_sessions.TryGetValue(user, out var cached))
        {
            return cached;
        }

        var token = await stack.ExchangedTokenAsync(user, "stalwart", cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, MailUrl + "/jmap/session");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await stack.Backchannel.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var session = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        var account = session.GetProperty("primaryAccounts").GetProperty("urn:ietf:params:jmap:mail").GetString()!;
        return _sessions[user] = (token, account);
    }
}

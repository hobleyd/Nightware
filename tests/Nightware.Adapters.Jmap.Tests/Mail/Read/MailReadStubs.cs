using System.Text.Json;
using System.Text.Json.Nodes;
using Nightware.Adapters.Jmap.Core.Protocol;
using Nightware.Adapters.Jmap.Tests.Support;
using WireMock.Matchers;
using WireMock.RequestBuilders;

namespace Nightware.Adapters.Jmap.Tests.Mail.Read;

/// <summary>WireMock answers for the mail read ports, keyed by the first method of each JMAP request.</summary>
internal static class MailReadStubs
{
    /// <summary>Alice's mailboxes as Stalwart creates them (ids as on a fresh volume), plus a child folder of the Inbox.</summary>
    public const string Mailboxes = """
        [["Mailbox/get",{"accountId":"c","state":"m1","list":[
          {"id":"a","name":"Inbox","parentId":null,"role":"inbox","sortOrder":0,"totalEmails":4,"unreadEmails":3},
          {"id":"c","name":"Junk Mail","parentId":null,"role":"junk","sortOrder":0,"totalEmails":0,"unreadEmails":0},
          {"id":"d","name":"Drafts","parentId":null,"role":"drafts","sortOrder":0,"totalEmails":1,"unreadEmails":0},
          {"id":"b","name":"Deleted Items","parentId":null,"role":"trash","sortOrder":0,"totalEmails":0,"unreadEmails":0},
          {"id":"e","name":"Sent Items","parentId":null,"role":"sent","sortOrder":0,"totalEmails":2,"unreadEmails":0},
          {"id":"f","name":"Projects","parentId":"a","role":null,"sortOrder":0,"totalEmails":12,"unreadEmails":1},
          {"id":"g","name":"Archive 2025","parentId":null,"role":null,"sortOrder":0,"totalEmails":0,"unreadEmails":0}],"notFound":[]},"c0"]]
        """;

    /// <summary>Answers API POSTs whose first method call is <paramref name="firstMethod"/>.</summary>
    public static void On(this JmapTestHost host, string firstMethod, string methodResponses) =>
        host.Server.Given(Request.Create().WithPath("/jmap/").UsingPost().WithBody(new WildcardMatcher($"*\"methodCalls\":[[\"{firstMethod}\"*")))
            .RespondWith(JmapTestHost.ApiResponse(methodResponses));

    public static void StubMailboxes(this JmapTestHost host) => host.On("Mailbox/get", Mailboxes);

    /// <summary>The method calls of every API POST whose first call is <paramref name="firstMethod"/>.</summary>
    public static IReadOnlyList<JsonArray> Calls(this JmapTestHost host, string firstMethod) =>
        [.. host.ApiBodies().Select(b => b["methodCalls"]!.AsArray()).Where(c => c[0]![0]!.GetValue<string>() == firstMethod)];

    /// <summary>An Email as Stalwart returns it for the list projection.</summary>
    public static string Email(string id, string mailbox = "a", string thread = "t1", string received = "2026-11-14T21:25:54Z", string keywords = "{}", string subject = "Hello") => $$"""
        {"id":"{{id}}","blobId":"B{{id}}","threadId":"{{thread}}","mailboxIds":{"{{mailbox}}":true},"keywords":{{keywords}},
         "messageId":["{{id}}@example.com"],"receivedAt":"{{received}}","sentAt":"{{received}}","subject":"{{subject}}",
         "from":[{"name":"Bob Brown","email":"bob@nightware.localhost"}],"sender":null,
         "to":[{"name":"Alice Anderson","email":"alice@nightware.localhost"}],"cc":null,"bcc":null,"replyTo":null,
         "preview":"Hi","hasAttachment":false,
         "bodyStructure":{"partId":"1","blobId":"P{{id}}","size":2,"type":"text/plain","subParts":null,"header:Content-Type":" text/plain; charset=utf-8"},
         "header:Importance:asText":null,"header:X-Priority:asText":null}
        """;

    public static string EmailGet(params string[] emails) =>
        $$"""{"accountId":"c","state":"s9","list":[{{string.Join(',', emails)}}],"notFound":[]}""";

    /// <summary>The recorded Email/get of a Nextcloud iMIP invitation (Stalwart v1.1.0, bob's Inbox).</summary>
    public static Email RecordedInvitation()
    {
        var responses = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "MailRead", "invitation-email-get.json")))!;
        return responses[0]![1]!["list"]![0].Deserialize(JmapJsonContext.Default.Email)!;
    }

    public static Email Parse(string emailJson) => JsonSerializer.Deserialize(emailJson, JmapJsonContext.Default.Email)!;
}

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Nightware.Integration.Tests.Directory;
using Nightware.Integration.Tests.Support;

namespace Nightware.Integration.Tests.Mail.Write;

/// <summary>
/// Phase 3 against the running stack: writes go through the façade as NightMail sends them, and their
/// effect is checked on Stalwart directly. Subjects and folder names are unique per run, so parallel runs
/// don't collide. Steps that need the read side (Phase 2's message locator and reader) skip while those
/// are still the 501 stubs.
/// </summary>
public sealed class MailWriteLiveTests : IAsyncLifetime
{
    private const string Alice = "alice";
    private const string Bob = "bob";
    private static readonly string BobAddress = DevStack.Address("bob");
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
        if (response.StatusCode == HttpStatusCode.NotImplemented)
        {
            Assert.Skip($"{method} {path} needs the read side (Phase 2): {text}");
        }

        return (response.StatusCode, text.Length == 0 ? null : JsonNode.Parse(text)!.AsObject());
    }

    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task SendMail_reaches_the_recipients_inbox_and_the_senders_sent_items()
    {
        var subject = $"Nightware sendMail {_run}";
        var alice = await _stack.GraphClientAsync(Alice, Ct);

        var (status, _) = await SendAsync(alice, HttpMethod.Post, "/v1.0/me/sendMail", $$$"""
            {"message":{"subject":"{{{subject}}}","body":{"contentType":"html","content":"<p>See attached.</p>"},
              "toRecipients":[{"emailAddress":{"address":"{{{BobAddress}}}"}}],
              "bccRecipients":[{"emailAddress":{"address":"{{{AliceAddress}}}"}}],
              "attachments":[{"@odata.type":"#microsoft.graph.fileAttachment","name":"q2.csv","contentType":"text/csv","contentBytes":"{{{Base64("q,revenue\n2,100\n")}}}"}]},
             "saveToSentItems":true}
            """);

        status.ShouldBe(HttpStatusCode.Accepted);
        var received = await _probe.WaitForAsync(Bob, "inbox", subject, Ct);
        received["attachments"]!.AsArray().ShouldHaveSingleItem()!["name"]!.GetValue<string>().ShouldBe("q2.csv");
        received["bcc"].ShouldBeNull("Bcc is not delivered");
        var sent = await _probe.WaitForAsync(Alice, "sent", subject, Ct);
        sent["keywords"]!.AsObject().ContainsKey("$draft").ShouldBeFalse();
        sent["bcc"]!.AsArray().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Compose_attach_and_send_a_draft()
    {
        var subject = $"Nightware draft {_run}";
        var alice = await _stack.GraphClientAsync(Alice, Ct);

        var (created, draft) = await SendAsync(alice, HttpMethod.Post, "/v1.0/me/messages", $$$"""
            {"subject":"{{{subject}}}","body":{"contentType":"Html","content":"<p>Draft text</p>"},"toRecipients":[{"emailAddress":{"address":"{{{BobAddress}}}"}}]}
            """);
        created.ShouldBe(HttpStatusCode.Created);
        var id = draft!["id"]!.GetValue<string>();
        id.ShouldStartWith("d.");

        (await SendAsync(alice, HttpMethod.Patch, $"/v1.0/me/messages/{id}", $$$"""
            {"subject":"{{{subject}}}","body":{"contentType":"Text","content":"Updated text"},"toRecipients":[{"emailAddress":{"address":"{{{BobAddress}}}"}}],"ccRecipients":[],"bccRecipients":[]}
            """)).Status.ShouldBe(HttpStatusCode.OK);
        (await SendAsync(alice, HttpMethod.Post, $"/v1.0/me/messages/{id}/attachments", $$$"""
            {"@odata.type":"#microsoft.graph.fileAttachment","name":"notes.txt","contentType":"text/plain","contentBytes":"{{{Base64("Meeting notes\n")}}}"}
            """)).Status.ShouldBe(HttpStatusCode.Created);
        (await SendAsync(alice, HttpMethod.Post, $"/v1.0/me/messages/{id}/send", null)).Status.ShouldBe(HttpStatusCode.Accepted);

        var received = await _probe.WaitForAsync(Bob, "inbox", subject, Ct);
        received["attachments"]!.AsArray().ShouldHaveSingleItem()!["name"]!.GetValue<string>().ShouldBe("notes.txt");
        await _probe.WaitForAsync(Alice, "sent", subject, Ct);
        (await _probe.RecentAsync(Alice, "drafts", """["subject"]""", Ct)).ShouldNotContain(e => e["subject"]!.GetValue<string>() == subject);
    }

    [Fact]
    public async Task Attachment_ids_listed_once_stay_valid_across_deletes_as_the_client_uses_them()
    {
        var subject = $"Nightware attachments {_run}";
        var alice = await _stack.GraphClientAsync(Alice, Ct);
        var (_, draft) = await SendAsync(alice, HttpMethod.Post, "/v1.0/me/messages", $$$"""
            {"subject":"{{{subject}}}","body":{"contentType":"Html","content":"<p>three files</p>"},"toRecipients":[{"emailAddress":{"address":"{{{BobAddress}}}"}}]}
            """);
        var id = draft!["id"]!.GetValue<string>();
        foreach (var (name, text) in new[] { ("a.txt", "first"), ("b.txt", "second"), ("c.txt", "third") })
        {
            (await SendAsync(alice, HttpMethod.Post, $"/v1.0/me/messages/{id}/attachments", $$$"""
                {"@odata.type":"#microsoft.graph.fileAttachment","name":"{{{name}}}","contentType":"text/plain","contentBytes":"{{{Base64(text)}}}"}
                """)).Status.ShouldBe(HttpStatusCode.Created);
        }

        // NightMail's _syncDraftAttachments / _removeExcludedFromForwardDraft: one listing, then several deletes.
        var listed = await ListAttachmentsAsync(alice, id);
        listed.Select(a => a.Name).ShouldBe(["a.txt", "b.txt", "c.txt"], ignoreOrder: true);
        foreach (var name in new[] { "a.txt", "c.txt" })
        {
            (await SendAsync(alice, HttpMethod.Delete, $"/v1.0/me/messages/{id}/attachments/{listed.Single(a => a.Name == name).Id}")).Status.ShouldBe(HttpStatusCode.NoContent);
        }

        var remaining = (await ListAttachmentsAsync(alice, id)).ShouldHaveSingleItem();
        remaining.ShouldBe(listed.Single(a => a.Name == "b.txt"), "the surviving file keeps the id it was listed with");
        using var content = await alice.GetAsync($"/v1.0/me/messages/{id}/attachments/{remaining.Id}/$value", Ct);
        (await content.Content.ReadAsStringAsync(Ct)).ShouldBe("second");
        (await SendAsync(alice, HttpMethod.Delete, $"/v1.0/me/messages/{id}")).Status.ShouldBe(HttpStatusCode.NoContent);
    }

    private static async Task<IReadOnlyList<(string Id, string Name)>> ListAttachmentsAsync(HttpClient client, string messageId)
    {
        var (status, body) = await SendAsync(client, HttpMethod.Get, $"/v1.0/me/messages/{messageId}/attachments?$select=id,name,size");
        status.ShouldBe(HttpStatusCode.OK);
        return [.. body!["value"]!.AsArray().Select(a => (a!["id"]!.GetValue<string>(), a["name"]!.GetValue<string>()))];
    }

    [Fact]
    public async Task A_reply_threads_into_the_same_conversation()
    {
        var subject = $"Nightware thread {_run}";
        var alice = await _stack.GraphClientAsync(Alice, Ct);
        (await SendAsync(alice, HttpMethod.Post, "/v1.0/me/sendMail", $$$"""
            {"message":{"subject":"{{{subject}}}","body":{"contentType":"text","content":"Question?"},"toRecipients":[{"emailAddress":{"address":"{{{BobAddress}}}"}}]},"saveToSentItems":true}
            """)).Status.ShouldBe(HttpStatusCode.Accepted);
        var original = await _probe.WaitForAsync(Bob, "inbox", subject, Ct);

        var bob = await _stack.GraphClientAsync(Bob, Ct);
        var (status, _) = await SendAsync(bob, HttpMethod.Post, $"/v1.0/me/messages/e.{original["id"]!.GetValue<string>()}/reply", """
            {"message":{"body":{"contentType":"text","content":"Answer.\n\n> Question?"}}}
            """);
        status.ShouldBe(HttpStatusCode.Accepted);

        var reply = await _probe.WaitForAsync(Alice, "inbox", "RE: " + subject, Ct);
        var sent = await _probe.WaitForAsync(Alice, "sent", subject, Ct);
        reply["threadId"]!.GetValue<string>().ShouldBe(sent["threadId"]!.GetValue<string>());
        reply["inReplyTo"]!.AsArray().ShouldHaveSingleItem()!.GetValue<string>().ShouldBe(sent["messageId"]![0]!.GetValue<string>());
        (await _probe.WaitForAsync(Bob, "sent", "RE: " + subject, Ct))["threadId"]!.GetValue<string>().ShouldBe(original["threadId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Folders_are_created_renamed_moved_and_deleted()
    {
        var alice = await _stack.GraphClientAsync(Alice, Ct);
        var name = $"Nightware {_run}";

        var (created, folder) = await SendAsync(alice, HttpMethod.Post, "/v1.0/me/mailFolders/msgfolderroot/childFolders", $$$"""{"displayName":"{{{name}}}"}""");
        created.ShouldBe(HttpStatusCode.Created);
        var id = folder!["id"]!.GetValue<string>();
        folder["parentFolderId"]!.GetValue<string>().ShouldBe("msgfolderroot");

        (await SendAsync(alice, HttpMethod.Post, "/v1.0/me/mailFolders/msgfolderroot/childFolders", $$$"""{"displayName":"{{{name.ToUpperInvariant()}}}"}""")).Status
            .ShouldBe(HttpStatusCode.Conflict);

        var (renamed, renamedFolder) = await SendAsync(alice, HttpMethod.Patch, $"/v1.0/me/mailFolders/{id}", $$$"""{"displayName":"{{{name}}} renamed"}""");
        renamed.ShouldBe(HttpStatusCode.OK);
        renamedFolder!["id"]!.GetValue<string>().ShouldBe(id);

        var (_, parent) = await SendAsync(alice, HttpMethod.Post, "/v1.0/me/mailFolders/inbox/childFolders", $$$"""{"displayName":"{{{name}}} parent"}""");
        var parentId = parent!["id"]!.GetValue<string>();
        (await SendAsync(alice, HttpMethod.Post, $"/v1.0/me/mailFolders/{id}/move", $$$"""{"destinationId":"{{{parentId}}}"}""")).Status.ShouldBe(HttpStatusCode.Created);
        (await _probe.MailboxesAsync(Alice, Ct)).Single(m => m["id"]!.GetValue<string>() == id)["parentId"]!.GetValue<string>().ShouldBe(parentId);
        (await SendAsync(alice, HttpMethod.Post, $"/v1.0/me/mailFolders/{id}/move", """{"destinationId":"msgfolderroot"}""")).Body!["parentFolderId"]!
            .GetValue<string>().ShouldBe("msgfolderroot");

        (await SendAsync(alice, HttpMethod.Post, $"/v1.0/me/mailFolders/{id}/move", $$$"""{"destinationId":"{{{parentId}}}"}""")).Status.ShouldBe(HttpStatusCode.Created);
        (await SendAsync(alice, HttpMethod.Delete, $"/v1.0/me/mailFolders/{parentId}")).Status.ShouldBe(HttpStatusCode.NoContent);
        (await _probe.MailboxesAsync(Alice, Ct)).ShouldNotContain(m => m["id"]!.GetValue<string>() == id || m["id"]!.GetValue<string>() == parentId);
    }

    [Fact]
    public async Task Migration_import_keeps_the_original_dates_sender_and_read_state()
    {
        var subject = $"Nightware import {_run}";
        var alice = await _stack.GraphClientAsync(Alice, Ct);

        var (status, imported) = await SendAsync(alice, HttpMethod.Post, "/v1.0/me/mailFolders/inbox/messages", $$$"""
            {"subject":"{{{subject}}}","body":{"contentType":"text","content":"Numbers attached."},
             "from":{"emailAddress":{"address":"alex@example.com","name":"Alex Sender"}},
             "toRecipients":[{"emailAddress":{"address":"{{{AliceAddress}}}"}}],
             "singleValueExtendedProperties":[{"id":"SystemTime 0x0E06","value":"2026-03-04T05:06:07.000Z"},{"id":"SystemTime 0x0039","value":"2026-03-04T05:00:00.000Z"},{"id":"Integer 0x0E07","value":"1"}],
             "isRead":true}
            """);

        status.ShouldBe(HttpStatusCode.Created);
        var id = imported!["id"]!.GetValue<string>();
        id.ShouldStartWith("e.");
        var stored = await _probe.GetAsync(Alice, id["e.".Length..], Ct);
        stored["subject"]!.GetValue<string>().ShouldBe(subject);
        var inbox = (await _probe.MailboxesAsync(Alice, Ct)).Single(m => m["role"]?.GetValue<string>() == "inbox")["id"]!.GetValue<string>();
        stored["mailboxIds"]!.AsObject().ContainsKey(inbox).ShouldBeTrue("the import lands in the folder it was posted to");
        stored["receivedAt"]!.GetValue<string>().ShouldBe("2026-03-04T05:06:07Z");
        stored["sentAt"]!.GetValue<string>().ShouldBe("2026-03-04T05:00:00Z");
        stored["keywords"]!.AsObject().ContainsKey("$seen").ShouldBeTrue();
        stored["keywords"]!.AsObject().ContainsKey("$draft").ShouldBeFalse();
        stored["from"]![0]!["email"]!.GetValue<string>().ShouldBe("alex@example.com");
    }
}

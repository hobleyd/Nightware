using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Nightware.Integration.Tests.Support;

namespace Nightware.Integration.Tests.Mail.Read;

/// <summary>Phase 2 against the running stack: alice's folders, delta, messages, attachments and an invitation.</summary>
public sealed class MailReadLiveTests
{
    private const string ListSelect = "id,subject,from,toRecipients,ccRecipients,bccRecipients,bodyPreview,isRead,flag,receivedDateTime,sentDateTime,importance,conversationId,hasAttachments,parentFolderId";
    private const string FolderSelect = "id,displayName,totalItemCount,unreadItemCount,parentFolderId,isHidden,childFolderCount";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonObject> GetJsonAsync(HttpClient client, string path, string? prefer = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (prefer is not null)
        {
            request.Headers.Add("Prefer", prefer);
        }

        using var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, text);
        return JsonNode.Parse(text)!.AsObject();
    }

    /// <summary>Walks a delta feed the way the client does, to its deltaLink; returns every item and the link.</summary>
    private static async Task<(List<JsonObject> Items, string DeltaLink)> WalkDeltaAsync(HttpClient client, string path)
    {
        var items = new List<JsonObject>();
        for (var pages = 0; pages < 500; pages++)
        {
            var page = await GetJsonAsync(client, path);
            items.AddRange(page["value"]!.AsArray().Select(static v => v!.AsObject()));
            if (page["@odata.deltaLink"]?.GetValue<string>() is { } delta)
            {
                return (items, MailLiveHost.Local(delta));
            }

            path = MailLiveHost.Local(page["@odata.nextLink"]!.GetValue<string>());
        }

        throw new ShouldAssertException("The delta walk never reached a deltaLink.");
    }

    [Fact]
    public async Task A_message_deleted_above_the_cursor_mid_sync_does_not_push_another_past_it()
    {
        await using var host = MailLiveHost.Start();
        using var graph = await host.GraphAsync(Ct);
        var (account, _) = await host.MailboxesAsync(Ct);
        var tag = TestMessages.Tag();
        using var created = await graph.PostAsJsonAsync("/v1.0/me/mailFolders/inbox/childFolders", new { displayName = "nw-anchor-" + tag }, Ct);
        created.EnsureSuccessStatusCode();
        var folder = (await created.Content.ReadFromJsonAsync<JsonObject>(Ct))!["id"]!.GetValue<string>();
        try
        {
            // Six messages a minute apart: newest first they are m6 … m1.
            var start = DateTimeOffset.UtcNow.AddHours(-1);
            var ids = new List<string>();
            for (var n = 1; n <= 6; n++)
            {
                ids.Add(await host.ImportAsync(account, folder, TestMessages.Plain($"{tag}-m{n}"), Ct, start.AddMinutes(n)));
            }

            var first = await GetJsonAsync(graph, $"/v1.0/me/mailFolders/{folder}/messages/delta?$select=subject&$top=2");
            var seen = first["value"]!.AsArray().Select(v => v!["subject"]!.GetValue<string>()).ToList();
            seen.ShouldBe([$"{tag}-m6", $"{tag}-m5"]);

            // Gone from above the cursor: by position, page two would start at m3 and m4 would never be sent.
            await host.SetAsync(account, ids[5], null, Ct);
            var (rest, _) = await WalkDeltaAsync(graph, MailLiveHost.Local(first["@odata.nextLink"]!.GetValue<string>()));
            seen.AddRange(rest.Where(i => !i.ContainsKey("@removed")).Select(i => i["subject"]!.GetValue<string>()));

            seen.ShouldBe([$"{tag}-m6", $"{tag}-m5", $"{tag}-m4", $"{tag}-m3", $"{tag}-m2", $"{tag}-m1"], ignoreOrder: true);
        }
        finally
        {
            using var _ = await graph.DeleteAsync($"/v1.0/me/mailFolders/{folder}", Ct);
        }
    }

    [Fact]
    public async Task Folders_hang_off_the_root_and_well_known_ids_match_parent_folder_ids()
    {
        await using var host = MailLiveHost.Start();
        using var graph = await host.GraphAsync(Ct);

        var folders = (await GetJsonAsync(graph, $"/v1.0/me/mailFolders?$select={FolderSelect}&$top=100"))["value"]!.AsArray().Select(static f => f!.AsObject()).ToList();
        var inboxId = (await GetJsonAsync(graph, "/v1.0/me/mailFolders/inbox?$select=id"))["id"]!.GetValue<string>();
        var root = await GetJsonAsync(graph, "/v1.0/me/mailFolders/msgfolderroot?$select=id");

        folders.Select(f => f["displayName"]!.GetValue<string>()).ShouldContain("Inbox");
        folders.ShouldAllBe(f => f["parentFolderId"]!.GetValue<string>() == root["id"]!.GetValue<string>());
        folders.Single(f => f["displayName"]!.GetValue<string>() == "Inbox")["id"]!.GetValue<string>().ShouldBe(inboxId);
        (await GetJsonAsync(graph, $"/v1.0/me/mailFolders/{inboxId}/childFolders?$select={FolderSelect}&$top=100"))["value"].ShouldNotBeNull();

        // The shared-mailbox probe for a mailbox alice was never given: 404/403, never 401 or 5xx.
        using var probe = await graph.GetAsync("/v1.0/users/" + Uri.EscapeDataString(DevStack.Address("bob")) + "/mailFolders/inbox?$select=id", Ct);
        probe.StatusCode.ShouldBeOneOf(HttpStatusCode.NotFound, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Inbox_delta_initial_then_incremental_sees_arrivals_reads_moves_and_deletions()
    {
        await using var host = MailLiveHost.Start();
        using var graph = await host.GraphAsync(Ct);
        var (account, roles) = await host.MailboxesAsync(Ct);

        var (initial, deltaLink) = await WalkDeltaAsync(graph, $"/v1.0/me/mailFolders/inbox/messages/delta?$select={ListSelect}&$top=50");
        initial.ShouldAllBe(i => i.ContainsKey("receivedDateTime") && !i.ContainsKey("@removed"));
        initial.Select(i => i["id"]!.GetValue<string>()).ShouldBeUnique();

        var tag = TestMessages.Tag();
        var arrived = await host.ImportAsync(account, roles["inbox"], TestMessages.Plain(tag), Ct);
        var read = await host.ImportAsync(account, roles["inbox"], TestMessages.Plain(tag + "-read"), Ct);
        var (arrivals, afterArrival) = await WalkDeltaAsync(graph, deltaLink);
        var item = arrivals.Single(i => i["subject"]?.GetValue<string>() == tag);
        item["id"]!.GetValue<string>().ShouldBe("e." + arrived);
        item["parentFolderId"]!.GetValue<string>().ShouldBe(roles["inbox"]);
        item["isRead"]!.GetValue<bool>().ShouldBeFalse();
        item.Select(p => p.Key).ShouldBe(ListSelect.Split(','), ignoreOrder: true, "the frozen $select still applies when following the link");

        var elsewhere = await host.ImportAsync(account, roles["trash"], TestMessages.Plain(tag + "-elsewhere"), Ct);
        (await WalkDeltaAsync(graph, afterArrival)).Items.ShouldNotContain(i => i["id"]!.GetValue<string>() == "e." + elsewhere);
        await host.SetAsync(account, read, new JsonObject { ["keywords/$seen"] = true }, Ct);
        await host.SetAsync(account, arrived, new JsonObject { [$"mailboxIds/{roles["inbox"]}"] = null, [$"mailboxIds/{roles["trash"]}"] = true }, Ct);
        await host.SetAsync(account, elsewhere, new JsonObject { ["keywords/$seen"] = true }, Ct);
        var (changes, afterMove) = await WalkDeltaAsync(graph, afterArrival);
        changes.Single(i => i["id"]!.GetValue<string>() == "e." + read)["isRead"]!.GetValue<bool>().ShouldBeTrue();
        changes.Single(i => i["id"]!.GetValue<string>() == "e." + arrived)["@removed"]!["reason"]!.GetValue<string>().ShouldBe("changed");
        changes.ShouldNotContain(i => i["id"]!.GetValue<string>() == "e." + elsewhere, "a change to a message filed elsewhere is not this folder's");
        await host.SetAsync(account, elsewhere, null, Ct);

        await host.SetAsync(account, read, null, Ct);
        await host.SetAsync(account, arrived, null, Ct);
        var (deletions, _) = await WalkDeltaAsync(graph, afterMove);
        deletions.Single(i => i["id"]!.GetValue<string>() == "e." + read)["@removed"]!["reason"]!.GetValue<string>().ShouldBe("deleted");

        // A tampered link is a 400 and a token for a vanished sync is a 410: both make the client re-bootstrap.
        using var garbage = await graph.GetAsync("/v1.0/me/mailFolders/inbox/messages/delta?$deltatoken=garbage", Ct);
        garbage.StatusCode.ShouldBe(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task A_message_with_attachments_reads_like_graph()
    {
        await using var host = MailLiveHost.Start();
        using var graph = await host.GraphAsync(Ct);
        var (account, roles) = await host.MailboxesAsync(Ct);
        var tag = TestMessages.Tag();
        var id = "e." + await host.ImportAsync(account, roles["inbox"], TestMessages.WithAttachments(tag), Ct);

        var message = await GetJsonAsync(graph, $"/v1.0/me/messages/{id}?$expand=attachments($select=id,name,contentType,size,isInline)", "outlook.timezone=\"UTC\"");

        message["subject"]!.GetValue<string>().ShouldBe(tag);
        message["importance"]!.GetValue<string>().ShouldBe("high");
        message["hasAttachments"]!.GetValue<bool>().ShouldBeTrue();
        message["body"]!["contentType"]!.GetValue<string>().ShouldBe("html");
        message["body"]!["content"]!.GetValue<string>().ShouldContain("cid:logo@nw");
        message.ContainsKey("@odata.type").ShouldBeFalse("an ordinary message is not an eventMessage");
        var attachments = message["attachments"]!.AsArray().Select(static a => a!.AsObject()).ToList();
        var logo = attachments.Single(a => a["contentType"]!.GetValue<string>() == "image/png");
        logo["isInline"]!.GetValue<bool>().ShouldBeTrue();
        var pdf = attachments.Single(a => a["name"]!.GetValue<string>() == "q2.pdf");
        pdf["@odata.type"]!.GetValue<string>().ShouldBe("#microsoft.graph.fileAttachment");
        var item = attachments.Single(a => a["@odata.type"]!.GetValue<string>() == "#microsoft.graph.itemAttachment");
        item["name"]!.GetValue<string>().ShouldBe("Inner note", "named after the attached message's subject, as Graph does");

        var inline = await GetJsonAsync(graph, $"/v1.0/me/messages/{id}/attachments/{logo["id"]}");
        inline["contentId"]!.GetValue<string>().ShouldBe("logo@nw");
        Convert.FromBase64String(inline["contentBytes"]!.GetValue<string>())[..4].ShouldBe(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        Encoding.ASCII.GetString(Convert.FromBase64String((await GetJsonAsync(graph, $"/v1.0/me/messages/{id}/attachments/{pdf["id"]}"))["contentBytes"]!.GetValue<string>())).ShouldStartWith("%PDF");
        (await GetJsonAsync(graph, $"/v1.0/me/messages/{id}/attachments/{item["id"]}")).ContainsKey("contentBytes").ShouldBeFalse();
        (await graph.GetStringAsync($"/v1.0/me/messages/{id}/attachments/{item["id"]}/$value", Ct)).ShouldContain("Subject: Inner note");

        var listed = (await GetJsonAsync(graph, $"/v1.0/me/messages/{id}/attachments?$select=id,name,contentType"))["value"]!.AsArray();
        listed.Count.ShouldBe(3);
        listed.Select(a => a!["name"]!.GetValue<string>()).ShouldContain("Inner note");

        var mime = await graph.GetStringAsync($"/v1.0/me/messages/{id}/$value", Ct);
        mime.ShouldContain($"Subject: {tag}");

        var probe = await GetJsonAsync(graph, $"/v1.0/me/messages/{id}?$select=internetMessageHeaders,body", "outlook.body-content-type=\"text\"");
        probe["body"]!["contentType"]!.GetValue<string>().ShouldBe("text");
        probe["body"]!["content"]!.GetValue<string>().ShouldContain("See the numbers.");
        probe["internetMessageHeaders"]!.AsArray().Select(h => h!["name"]!.GetValue<string>()).ShouldContain("Subject");

        await host.SetAsync(account, id[2..], null, Ct);
    }

    [Fact]
    public async Task An_invitation_is_an_event_message_request_with_its_meeting_facts()
    {
        await using var host = MailLiveHost.Start();
        using var graph = await host.GraphAsync(Ct);
        var (account, roles) = await host.MailboxesAsync(Ct);
        var tag = TestMessages.Tag();
        var jmapId = await host.ImportAsync(account, roles["inbox"], TestMessages.Invitation(tag), Ct);

        var message = await GetJsonAsync(graph, $"/v1.0/me/messages/e.{jmapId}?$expand=attachments($select=id,name,contentType,size,isInline)", "outlook.timezone=\"UTC\"");

        message["@odata.type"]!.GetValue<string>().ShouldBe("#microsoft.graph.eventMessageRequest");
        message["meetingMessageType"]!.GetValue<string>().ShouldBe("meetingRequest");
        message["startDateTime"]!.ToJsonString().ShouldBe("""{"dateTime":"2026-12-01T09:00:00.0000000","timeZone":"UTC"}""");
        message["endDateTime"]!["dateTime"]!.GetValue<string>().ShouldBe("2026-12-01T10:00:00.0000000");
        message["location"]!["displayName"]!.GetValue<string>().ShouldBe("Boardroom");
        message["body"]!["contentType"]!.GetValue<string>().ShouldBe("text");
        message["attachments"]!.AsArray().Select(a => a!["contentType"]!.GetValue<string>()).ShouldContain("text/calendar");

        await host.SetAsync(account, jmapId, null, Ct);
    }

    [Fact]
    public async Task Folder_pages_search_and_conversation_expansion()
    {
        await using var host = MailLiveHost.Start();
        using var graph = await host.GraphAsync(Ct);
        var (account, roles) = await host.MailboxesAsync(Ct);
        var tag = TestMessages.Tag();
        var ids = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add(await host.ImportAsync(account, roles["inbox"], TestMessages.Plain($"{tag} {i}"), Ct));
        }

        var page = await GetJsonAsync(graph, $"/v1.0/me/mailFolders/inbox/messages?$top=2&$skip=0&$select={ListSelect}&$orderby=receivedDateTime desc");
        page["value"]!.AsArray().Count.ShouldBe(2);
        var next = MailLiveHost.Local(page["@odata.nextLink"]!.GetValue<string>());
        (await GetJsonAsync(graph, next))["value"]!.AsArray().Count.ShouldBeGreaterThan(0);

        var found = await GetJsonAsync(graph, $"/v1.0/me/messages?$search=\"subject:{tag}\"&$top=50&$select={ListSelect}");
        found["value"]!.AsArray().Count.ShouldBe(3);

        var first = found["value"]![0]!;
        var conversation = first["conversationId"]!.GetValue<string>();
        var expanded = await GetJsonAsync(graph, $"/v1.0/me/messages?$filter=conversationId in ('{conversation}') and parentFolderId ne '{roles["trash"]}'&$select={ListSelect}&$top=200");
        expanded["value"]!.AsArray().Select(m => m!["conversationId"]!.GetValue<string>()).ShouldAllBe(c => c == conversation);
        expanded["value"]!.AsArray().Select(m => m!["id"]!.GetValue<string>()).ShouldContain(first["id"]!.GetValue<string>());

        var unread = await GetJsonAsync(graph, $"/v1.0/me/messages?$filter=isRead eq false&$top=25&$skip=0&$select=id,isRead&$orderby=receivedDateTime desc");
        unread["value"]!.AsArray().ShouldAllBe(m => !m!["isRead"]!.GetValue<bool>());

        foreach (var id in ids)
        {
            await host.SetAsync(account, id, null, Ct);
        }
    }

    [Fact]
    public async Task People_ranking_sees_whom_alice_recently_wrote_to()
    {
        await using var host = MailLiveHost.Start();
        using var graph = await host.GraphAsync(Ct);
        var (account, roles) = await host.MailboxesAsync(Ct);
        var tag = TestMessages.Tag();
        var recipient = $"{tag}@example.com";
        var sent = await host.ImportAsync(account, roles["sent"], TestMessages.Sent(tag, recipient), Ct);

        try
        {
            var people = await GetJsonAsync(graph, "/v1.0/me/people?$select=displayName,scoredEmailAddresses&$top=999");

            people["value"]!.AsArray()
                .SelectMany(p => p!["scoredEmailAddresses"]!.AsArray().Select(a => a!["address"]!.GetValue<string>()))
                .ShouldContain(recipient, "IRecentCorrespondents feeds /people from Sent Items");
        }
        finally
        {
            await host.SetAsync(account, sent, null, Ct);
        }
    }
}

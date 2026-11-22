using System.Net;
using System.Text;
using System.Text.Json.Nodes;

using Nightware.Integration.Tests.Directory;
using Nightware.Integration.Tests.Support;

namespace Nightware.Integration.Tests.Parity;

/// <summary>Phase 19 against the dev stack: a draft created from MIME, and a message sent as MIME to oneself.</summary>
public sealed class MimeLiveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static StringContent Mime(string to, string subject)
    {
        var message = $"To: {to}\r\nSubject: {subject}\r\nMIME-Version: 1.0\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nSent as MIME.\r\n";
        var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(message));
        return new StringContent(string.Join("\r\n", base64.Chunk(76).Select(static c => new string(c))), Encoding.ASCII, "text/plain");
    }

    private static async Task<JsonObject> GetAsync(HttpClient client, string path) => JsonNode.Parse(await client.GetStringAsync(path, Ct))!.AsObject();

    [Fact]
    public async Task A_MIME_draft_is_saved_and_a_MIME_message_is_delivered()
    {
        Assert.SkipUnless(DevStack.Enabled, "Set NIGHTWARE_INTEGRATION=1 with the dev stack running.");
        await using var stack = LiveStack.Start();
        using var bob = await stack.GraphClientAsync("bob", Ct);
        var me = (await GetAsync(bob, "/v1.0/me?$select=mail"))["mail"]!.GetValue<string>();
        var tag = "nw-mime-" + Guid.NewGuid().ToString("N")[..8];

        using (var created = await bob.PostAsync("/v1.0/me/messages", Mime(me, tag + " draft"), Ct))
        {
            var text = await created.Content.ReadAsStringAsync(Ct);
            created.StatusCode.ShouldBe(HttpStatusCode.Created, text);
            var draft = JsonNode.Parse(text)!;
            draft["subject"]!.GetValue<string>().ShouldBe(tag + " draft");
            draft["isDraft"]!.GetValue<bool>().ShouldBeTrue();
            var id = Uri.EscapeDataString(draft["id"]!.GetValue<string>());
            (await GetAsync(bob, $"/v1.0/me/messages/{id}?$select=toRecipients"))["toRecipients"]![0]!["emailAddress"]!["address"]!.GetValue<string>()
                .ShouldBe(me, StringCompareShould.IgnoreCase);
            using var deleted = await bob.DeleteAsync($"/v1.0/me/messages/{id}", Ct);
            deleted.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        }

        using (var bad = await bob.PostAsync("/v1.0/me/sendMail", new StringContent("not base64!", Encoding.ASCII, "text/plain"), Ct))
        {
            bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await bad.Content.ReadAsStringAsync(Ct)).ShouldContain("ErrorMimeContentInvalidBase64String");
        }

        using (var sent = await bob.PostAsync("/v1.0/me/sendMail", Mime(me, tag + " sent"), Ct))
        {
            sent.StatusCode.ShouldBe(HttpStatusCode.Accepted, await sent.Content.ReadAsStringAsync(Ct));
        }

        // It is filed in Sent Items and delivered to the Inbox (bob sent it to himself).
        foreach (var folder in new[] { "sentitems", "inbox" })
        {
            JsonNode? found = null;
            for (var attempt = 0; attempt < 30 && found is null; attempt++)
            {
                var page = await GetAsync(bob, $"/v1.0/me/mailFolders/{folder}/messages?$select=id,subject&$top=25");
                found = page["value"]!.AsArray().FirstOrDefault(m => m!["subject"]!.GetValue<string>() == tag + " sent");
                if (found is null)
                {
                    await Task.Delay(1000, Ct);
                }
            }

            found.ShouldNotBeNull($"the MIME message in {folder}");
            using var _ = await bob.DeleteAsync($"/v1.0/me/messages/{Uri.EscapeDataString(found["id"]!.GetValue<string>())}", Ct);
        }
    }
}

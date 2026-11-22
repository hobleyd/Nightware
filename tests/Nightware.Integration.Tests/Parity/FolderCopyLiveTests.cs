using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

using Nightware.Integration.Tests.Directory;
using Nightware.Integration.Tests.Support;

namespace Nightware.Integration.Tests.Parity;

/// <summary>Phase 19 against the dev stack: a top-level folder create and a folder copy with its subfolder and mail.</summary>
public sealed class FolderCopyLiveTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<JsonObject> SendAsync(HttpClient client, HttpMethod method, string path, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        using var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, text);
        return text.Length == 0 ? [] : JsonNode.Parse(text)!.AsObject();
    }

    private static string Id(JsonObject o) => Uri.EscapeDataString(o["id"]!.GetValue<string>());

    [Fact]
    public async Task A_copied_folder_holds_copies_of_its_mail_and_subfolders()
    {
        Assert.SkipUnless(DevStack.Enabled, "Set NIGHTWARE_INTEGRATION=1 with the dev stack running.");
        await using var stack = LiveStack.Start();
        using var bob = await stack.GraphClientAsync("bob", Ct);
        var tag = "nw-copy-" + Guid.NewGuid().ToString("N")[..8];

        var folder = await SendAsync(bob, HttpMethod.Post, "/v1.0/me/mailFolders", new { displayName = tag }, HttpStatusCode.Created);
        JsonObject? copy = null;
        try
        {
            folder["parentFolderId"]!.GetValue<string>().ShouldNotBeNullOrEmpty();
            (await SendAsync(bob, HttpMethod.Post, "/v1.0/me/mailFolders", new { displayName = tag }, HttpStatusCode.Conflict))
                .ShouldNotBeNull();
            var child = await SendAsync(bob, HttpMethod.Post, $"/v1.0/me/mailFolders/{Id(folder)}/childFolders", new { displayName = "kid" }, HttpStatusCode.Created);

            // One message in the folder, one in the child, stored there directly: no delivery to wait for.
            foreach (var (target, subject) in new[] { (folder, tag + " top"), (child, tag + " kid") })
            {
                await SendAsync(bob, HttpMethod.Post, $"/v1.0/me/mailFolders/{Id(target)}/messages",
                    new { subject, body = new { contentType = "text", content = "copy me" } }, HttpStatusCode.Created);
            }

            copy = await SendAsync(bob, HttpMethod.Post, $"/v1.0/me/mailFolders/{Id(folder)}/copy", new { destinationId = "inbox" });
            copy["displayName"]!.GetValue<string>().ShouldBe(tag);
            copy["id"]!.GetValue<string>().ShouldNotBe(folder["id"]!.GetValue<string>());
            copy["totalItemCount"]!.GetValue<int>().ShouldBe(1);

            var kids = await SendAsync(bob, HttpMethod.Get, $"/v1.0/me/mailFolders/{Id(copy)}/childFolders");
            var kidCopy = kids["value"]!.AsArray().ShouldHaveSingleItem()!.AsObject();
            kidCopy["displayName"]!.GetValue<string>().ShouldBe("kid");
            var kidMail = await SendAsync(bob, HttpMethod.Get, $"/v1.0/me/mailFolders/{Id(kidCopy)}/messages?$select=subject");
            kidMail["value"]!.AsArray().ShouldHaveSingleItem()!["subject"]!.GetValue<string>().ShouldBe(tag + " kid");

            // The original is untouched.
            (await SendAsync(bob, HttpMethod.Get, $"/v1.0/me/mailFolders/{Id(folder)}"))["totalItemCount"]!.GetValue<int>().ShouldBe(1);
            await SendAsync(bob, HttpMethod.Post, $"/v1.0/me/mailFolders/{Id(folder)}/copy", new { destinationId = child["id"]!.GetValue<string>() }, HttpStatusCode.BadRequest);
        }
        finally
        {
            if (copy is not null)
            {
                await SendAsync(bob, HttpMethod.Delete, $"/v1.0/me/mailFolders/{Id(copy)}", expected: HttpStatusCode.NoContent);
            }

            await SendAsync(bob, HttpMethod.Delete, $"/v1.0/me/mailFolders/{Id(folder)}", expected: HttpStatusCode.NoContent);
        }
    }
}

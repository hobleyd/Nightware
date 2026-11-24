using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

using Nightware.Integration.Tests.Directory;
using Nightware.Integration.Tests.Support;

namespace Nightware.Integration.Tests.Parity;

/// <summary>Phase 19 against the dev stack: setting, reading and removing one's own photo (a Nextcloud avatar).</summary>
public sealed class PhotoLiveTests
{
    /// <summary>An 8x8 JPEG (the capture tool's contact photo).</summary>
    private static readonly byte[] Jpeg = Convert.FromBase64String("/9j/4AAQSkZJRgABAQAASABIAAD/4QBMRXhpZgAATU0AKgAAAAgAAYdpAAQAAAABAAAAGgAAAAAAA6ABAAMAAAABAAEAAKACAAQAAAABAAAACKADAAQAAAABAAAACAAAAAD/7QA4UGhvdG9zaG9wIDMuMAA4QklNBAQAAAAAAAA4QklNBCUAAAAAABDUHYzZjwCyBOmACZjs+EJ+/8AAEQgACAAIAwEiAAIRAQMRAf/EAB8AAAEFAQEBAQEBAAAAAAAAAAABAgMEBQYHCAkKC//EALUQAAIBAwMCBAMFBQQEAAABfQECAwAEEQUSITFBBhNRYQcicRQygZGhCCNCscEVUtHwJDNicoIJChYXGBkaJSYnKCkqNDU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6g4SFhoeIiYqSk5SVlpeYmZqio6Slpqeoqaqys7S1tre4ubrCw8TFxsfIycrS09TV1tfY2drh4uPk5ebn6Onq8fLz9PX29/j5+v/EAB8BAAMBAQEBAQEBAQEAAAAAAAABAgMEBQYHCAkKC//EALURAAIBAgQEAwQHBQQEAAECdwABAgMRBAUhMQYSQVEHYXETIjKBCBRCkaGxwQkjM1LwFWJy0QoWJDThJfEXGBkaJicoKSo1Njc4OTpDREVGR0hJSlNUVVZXWFlaY2RlZmdoaWpzdHV2d3h5eoKDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uLj5OXm5+jp6vLz9PX29/j5+v/bAEMABAQEBAQEBgQEBgkGBgYJDAkJCQkMDwwMDAwMDxIPDw8PDw8SEhISEhISEhUVFRUVFRkZGRkZHBwcHBwcHBwcHP/bAEMBBAUFBwcHDAcHDB0UEBQdHR0dHR0dHR0dHR0dHR0dHR0dHR0dHR0dHR0dHR0dHR0dHR0dHR0dHR0dHR0dHR0dHf/dAAQAAf/aAAwDAQACEQMRAD8A8looor9WPgT/2Q==");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<HttpResponseMessage> PutAsync(HttpClient client, string path, byte[] image)
    {
        var content = new ByteArrayContent(image);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        return await client.PutAsync(path, content, Ct);
    }

    [Fact]
    public async Task The_caller_sets_reads_and_removes_their_own_photo()
    {
        Assert.SkipUnless(DevStack.Enabled, "Set NIGHTWARE_INTEGRATION=1 with the dev stack running.");
        await using var stack = LiveStack.Start();
        using var bob = await stack.GraphClientAsync("bob", Ct);

        // Whatever bob had is put back at the end (a square avatar can be re-uploaded as it came).
        using var before = await bob.GetAsync("/v1.0/me/photo/$value", Ct);
        var original = before.StatusCode == HttpStatusCode.OK ? await before.Content.ReadAsByteArrayAsync(Ct) : null;
        try
        {
            using (var set = await PutAsync(bob, "/v1.0/me/photo/$value", Jpeg))
            {
                set.StatusCode.ShouldBe(HttpStatusCode.OK, await set.Content.ReadAsStringAsync(Ct));
            }

            using (var value = await bob.GetAsync("/v1.0/me/photo/$value", Ct))
            {
                value.StatusCode.ShouldBe(HttpStatusCode.OK);
                value.Content.Headers.ContentType!.MediaType.ShouldStartWith("image/");
            }

            var meta = JsonNode.Parse(await bob.GetStringAsync("/v1.0/me/photo", Ct))!;
            meta["width"]!.GetValue<int>().ShouldBe(meta["height"]!.GetValue<int>());
            meta["width"]!.GetValue<int>().ShouldBeGreaterThan(1);
            meta["@odata.mediaEtag"]!.GetValue<string>().ShouldNotBeNullOrEmpty();

            // Another user sees it through /users/{bob}.
            using var alice = await stack.GraphClientAsync("alice", Ct);
            var bobAddress = JsonNode.Parse(await bob.GetStringAsync("/v1.0/me?$select=mail", Ct))!["mail"]!.GetValue<string>();
            JsonNode.Parse(await alice.GetStringAsync($"/v1.0/users/{Uri.EscapeDataString(bobAddress)}/photo", Ct))!["id"]!.GetValue<string>()
                .ShouldBe(meta["id"]!.GetValue<string>());
            using (var theirs = await PutAsync(alice, $"/v1.0/users/{Uri.EscapeDataString(bobAddress)}/photo/$value", Jpeg))
            {
                theirs.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
            }

            using (var removed = await bob.DeleteAsync("/v1.0/me/photo/$value", Ct))
            {
                removed.StatusCode.ShouldBe(HttpStatusCode.OK);
            }

            using (var gone = await bob.GetAsync("/v1.0/me/photo/$value", Ct))
            {
                gone.StatusCode.ShouldBe(HttpStatusCode.NotFound);
            }

            JsonNode.Parse(await bob.GetStringAsync("/v1.0/me/photo", Ct))!["id"]!.GetValue<string>().ShouldBe("1x1");
        }
        finally
        {
            if (original is not null)
            {
                using var restored = await PutAsync(bob, "/v1.0/me/photo/$value", original);
            }
        }
    }

    [Fact]
    public async Task A_wide_photo_in_another_format_is_cropped_to_a_square_avatar()
    {
        Assert.SkipUnless(DevStack.Enabled, "Set NIGHTWARE_INTEGRATION=1 with the dev stack running.");
        await using var stack = LiveStack.Start();
        using var bob = await stack.GraphClientAsync("bob", Ct);
        using var before = await bob.GetAsync("/v1.0/me/photo/$value", Ct);
        var original = before.StatusCode == HttpStatusCode.OK ? await before.Content.ReadAsByteArrayAsync(Ct) : null;

        // 600 x 300 WebP: neither square nor a type Nextcloud keeps (phase 27 crops and converts it).
        using var bitmap = new SkiaSharp.SKBitmap(600, 300);
        bitmap.Erase(SkiaSharp.SKColors.Coral);
        using var webp = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Webp, 90);
        try
        {
            using (var set = await PutAsync(bob, "/v1.0/me/photo/$value", webp.ToArray()))
            {
                set.StatusCode.ShouldBe(HttpStatusCode.OK, await set.Content.ReadAsStringAsync(Ct));
            }

            var meta = JsonNode.Parse(await bob.GetStringAsync("/v1.0/me/photo", Ct))!;
            meta["width"]!.GetValue<int>().ShouldBe(meta["height"]!.GetValue<int>());
            meta["width"]!.GetValue<int>().ShouldBeGreaterThan(1);
        }
        finally
        {
            using var restored = original is null ? await bob.DeleteAsync("/v1.0/me/photo/$value", Ct) : await PutAsync(bob, "/v1.0/me/photo/$value", original);
        }
    }
}

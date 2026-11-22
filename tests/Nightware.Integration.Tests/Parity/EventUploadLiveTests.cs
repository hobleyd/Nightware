using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

using Nightware.Integration.Tests.Directory;
using Nightware.Integration.Tests.Support;

namespace Nightware.Integration.Tests.Parity;

/// <summary>Phase 19 against the dev stack: a large event attachment uploaded in chunks, then read back through $value.</summary>
public sealed class EventUploadLiveTests
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

    [Fact]
    public async Task A_chunked_upload_becomes_an_attachment_whose_value_is_the_file()
    {
        Assert.SkipUnless(DevStack.Enabled, "Set NIGHTWARE_INTEGRATION=1 with the dev stack running.");
        await using var stack = LiveStack.Start();
        using var bob = await stack.GraphClientAsync("bob", Ct);
        var start = DateTime.UtcNow.Date.AddDays(4).AddHours(9);
        var created = await SendAsync(bob, HttpMethod.Post, "/v1.0/me/events", new
        {
            subject = "nw-upload-" + Guid.NewGuid().ToString("N")[..8],
            start = new { dateTime = start.ToString("s"), timeZone = "UTC" },
            end = new { dateTime = start.AddHours(1).ToString("s"), timeZone = "UTC" },
        }, HttpStatusCode.Created);
        var eventId = Uri.EscapeDataString(created["id"]!.GetValue<string>());
        try
        {
            // 5 MB in two chunks: 4 MB (a multiple of 320 KiB, as Graph clients send), then the rest.
            var file = RandomNumberGenerator.GetBytes(5 * 1024 * 1024 + 123);
            var session = await SendAsync(bob, HttpMethod.Post, $"/v1.0/me/events/{eventId}/attachments/createUploadSession",
                new { AttachmentItem = new { attachmentType = "file", name = "big.bin", size = file.Length } }, HttpStatusCode.Created);
            var uploadUrl = session["uploadUrl"]!.GetValue<string>();
            // The upload URL carries its own credential; the client sends no bearer, as Graph clients do.
            using var anonymous = await stack.GraphClientAsync("bob", Ct);
            anonymous.DefaultRequestHeaders.Authorization = null;

            const int firstChunk = 327_680 * 12;
            foreach (var (from, to, status) in new[] { (0, firstChunk - 1, HttpStatusCode.OK), (firstChunk, file.Length - 1, HttpStatusCode.Created) })
            {
                var content = new ByteArrayContent(file, from, to - from + 1);
                content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, file.Length);
                using var put = await anonymous.PutAsync(LiveStack.Local(uploadUrl), content, Ct);
                var text = await put.Content.ReadAsStringAsync(Ct);
                put.StatusCode.ShouldBe(status, text);
                if (status == HttpStatusCode.OK)
                {
                    JsonNode.Parse(text)!["nextExpectedRanges"]![0]!.GetValue<string>().ShouldBe(firstChunk.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                else
                {
                    put.Headers.Location.ShouldNotBeNull();
                }
            }

            var attachment = (await SendAsync(bob, HttpMethod.Get, $"/v1.0/me/events/{eventId}/attachments"))["value"]!.AsArray()
                .ShouldHaveSingleItem()!;
            attachment["name"]!.GetValue<string>().ShouldBe("big.bin");
            var value = await bob.GetByteArrayAsync($"/v1.0/me/events/{eventId}/attachments/{Uri.EscapeDataString(attachment["id"]!.GetValue<string>())}/$value", Ct);
            SHA256.HashData(value).ShouldBe(SHA256.HashData(file));
        }
        finally
        {
            await SendAsync(bob, HttpMethod.Delete, $"/v1.0/me/events/{eventId}", expected: HttpStatusCode.NoContent);
        }
    }
}

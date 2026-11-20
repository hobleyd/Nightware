using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Nightware.Integration.Tests.Directory;
using Nightware.Integration.Tests.Support;

namespace Nightware.Integration.Tests.Mail.Write;

/// <summary>Phase 12 large attachments against the running stack: chunks staged as Stalwart blobs, attached on the last.</summary>
public sealed class DraftUploadLiveTests : IAsyncLifetime
{
    private const string Alice = "alice";
    private LiveStack _stack = null!;
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _stack = LiveStack.Start();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_stack is not null)
        {
            await _stack.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_ten_megabyte_file_uploaded_in_chunks_becomes_one_attachment_of_the_draft()
    {
        var alice = await _stack.GraphClientAsync(Alice, Ct);
        var draft = JsonNode.Parse(await (await alice.PostAsync("/v1.0/me/messages",
            new StringContent($$"""{"subject":"Nightware upload {{_run}}"}""", Encoding.UTF8, "application/json"), Ct)).Content.ReadAsStringAsync(Ct))!;
        var draftId = draft["id"]!.GetValue<string>();
        try
        {
            const int size = 10 * 1024 * 1024;
            var file = new byte[size];
            new Random(42).NextBytes(file);

            var begun = await alice.PostAsync($"/v1.0/me/messages/{draftId}/attachments/createUploadSession",
                new StringContent($$$"""{"AttachmentItem":{"attachmentType":"file","name":"big-{{{_run}}}.bin","size":{{{size}}}}}""", Encoding.UTF8, "application/json"), Ct);
            begun.StatusCode.ShouldBe(HttpStatusCode.Created, await begun.Content.ReadAsStringAsync(Ct));
            var uploadUrl = JsonNode.Parse(await begun.Content.ReadAsStringAsync(Ct))!["uploadUrl"]!.GetValue<string>();

            // The upload URL carries its own credential; the client sends no bearer, as Graph clients do.
            using var anonymous = await _stack.GraphClientAsync(Alice, Ct);
            anonymous.DefaultRequestHeaders.Authorization = null;
            const int chunkSize = 327_680 * 10;
            HttpResponseMessage? last = null;
            for (var first = 0; first < size; first += chunkSize)
            {
                var length = Math.Min(chunkSize, size - first);
                using var content = new ByteArrayContent(file, first, length);
                content.Headers.ContentRange = new ContentRangeHeaderValue(first, first + length - 1, size);
                last = await anonymous.PutAsync(LiveStack.Local(uploadUrl), content, Ct);
                var expected = first + length < size ? HttpStatusCode.OK : HttpStatusCode.Created;
                last.StatusCode.ShouldBe(expected, await last.Content.ReadAsStringAsync(Ct));
            }

            last!.Headers.Location.ShouldNotBeNull();
            var attachments = JsonNode.Parse(await alice.GetStringAsync($"/v1.0/me/messages/{draftId}/attachments?$select=id,name,contentType,size", Ct))!;
            var attachment = attachments["value"]!.AsArray().Single()!;
            attachment["name"]!.GetValue<string>().ShouldBe($"big-{_run}.bin");
            var id = attachment["id"]!.GetValue<string>();
            var bytes = await alice.GetByteArrayAsync($"/v1.0/me/messages/{draftId}/attachments/{id}/$value", Ct);
            bytes.ShouldBe(file);
        }
        finally
        {
            await alice.PostAsync($"/v1.0/me/messages/{draftId}/permanentDelete", null, Ct);
        }
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Nightware.Api.Tests.Support;
using Nightware.Application.Mail;
using Nightware.Application.Mail.Write;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;

namespace Nightware.Api.Tests.Features.Mail.Write;

public sealed class MailUploadEndpointTests : IAsyncLifetime
{
    private const string Draft = "d.ZHJhZnQ";
    private static readonly DraftUploadFile File = new("big.bin", "application/octet-stream", 10);
    private readonly IDraftUploadStaging _staging = Substitute.For<IDraftUploadStaging>();
    private readonly IDraftStore _drafts = Substitute.For<IDraftStore>();
    private TestHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _staging.BeginAsync(default, default, default!, default).ReturnsForAnyArgs(new DraftUploadState("s1", File, 0));
        _host = await TestHost.StartAsync(services => services.AddSingleton(_staging).AddSingleton(_drafts));
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private async Task<string> BeginAsync()
    {
        var response = await _host.AuthorizedClient().PostAsync($"/v1.0/me/messages/{Draft}/attachments/createUploadSession",
            new StringContent("""{"AttachmentItem":{"attachmentType":"file","name":"big.bin","size":10}}""", Encoding.UTF8, "application/json"), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await response.ReadObjectAsync();
        body["nextExpectedRanges"]!.AsArray().Single()!.GetValue<string>().ShouldBe("0-");
        body["expirationDateTime"].ShouldNotBeNull();
        return body["uploadUrl"]!.GetValue<string>();
    }

    private Task<HttpResponseMessage> PutAsync(string uploadUrl, int first, int length, int total = 10)
    {
        var content = new ByteArrayContent(new byte[length]);
        content.Headers.ContentRange = new ContentRangeHeaderValue(first, first + length - 1, total);
        return _host.Client.PutAsync(uploadUrl[TestHost.GraphBase.Length..].Insert(0, "/v1.0"), content, Ct);
    }

    [Fact]
    public async Task The_upload_url_names_the_draft_and_session_on_the_graph_base()
    {
        var url = await BeginAsync();

        url.ShouldStartWith($"{TestHost.GraphBase}/users/me@example.com/messages/{Draft}/attachmentSessions/s1/content?authtoken=");
        await _staging.Received(1).BeginAsync(new MailboxRef(TestHost.UserEmail), new MessageId(Draft), File, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_middle_chunk_without_a_bearer_answers_the_next_byte_and_the_last_201_with_a_location()
    {
        var url = await BeginAsync();
        _staging.AppendAsync(new MailboxRef(TestHost.UserEmail), new MessageId(Draft), "s1", 0, 4, 10, Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new DraftUploadState("s1", File, 4));
        _staging.AppendAsync(new MailboxRef(TestHost.UserEmail), new MessageId(Draft), "s1", 4, 6, 10, Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new DraftUploadState("s1", File, 10));
        _staging.ReadAsync(default, default, default!, Ct).ReturnsForAnyArgs(new ReadOnlyMemory<byte>(new byte[10]));
        _drafts.AddAttachmentAsync(default, default, default!, Ct).ReturnsForAnyArgs(new MailAttachment(new AttachmentId("a1"), "big.bin", "application/octet-stream", 10, AttachmentKind.File));

        var middle = await PutAsync(url, 0, 4);
        middle.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await middle.ReadObjectAsync())["nextExpectedRanges"]!.AsArray().Single()!.GetValue<string>().ShouldBe("4");

        var last = await PutAsync(url, 4, 6);
        last.StatusCode.ShouldBe(HttpStatusCode.Created);
        last.Headers.Location!.ToString().ShouldBe($"{TestHost.GraphBase}/users('me%40example.com')/messages('{Draft}')/attachments('a1')");
    }

    [Fact]
    public async Task A_chunk_without_a_content_range_is_400_and_a_forged_url_401()
    {
        var url = await BeginAsync();

        (await _host.Client.PutAsync(url[TestHost.GraphBase.Length..].Insert(0, "/v1.0"), new ByteArrayContent([1]), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var forged = url[..^4] + (url[^4] == 'A' ? "B" : "A") + url[^3..];
        (await PutAsync(forged, 0, 4)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_session_for_something_other_than_a_file_is_refused()
    {
        var response = await _host.AuthorizedClient().PostAsync($"/v1.0/me/messages/{Draft}/attachments/createUploadSession",
            new StringContent("""{"AttachmentItem":{"attachmentType":"item","name":"x","size":1}}""", Encoding.UTF8, "application/json"), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotImplemented);
    }
}

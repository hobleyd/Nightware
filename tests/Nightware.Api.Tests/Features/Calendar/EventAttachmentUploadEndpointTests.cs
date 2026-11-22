using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Nightware.Api.Tests.Support;
using Nightware.Application.Calendar.Calendars;
using Nightware.Domain.Calendar;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Tests.Features.Calendar;

/// <summary>Phase 19: an event attachment's $value, and large event attachments in chunks through a sealed upload URL.</summary>
public sealed class EventAttachmentUploadEndpointTests : IAsyncLifetime
{
    private const string Event = "ev-1";
    private static readonly EventUploadFile File = new("big.bin", "application/pdf", 10);
    private static readonly MailboxRef Me = new(TestHost.UserEmail);
    private readonly IEventAttachmentUploads _uploads = Substitute.For<IEventAttachmentUploads>();
    private readonly IEventAttachmentStore _store = Substitute.For<IEventAttachmentStore>();
    private TestHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _uploads.BeginAsync(default, default!, default!, default).ReturnsForAnyArgs(new EventUploadState("s1", File, 0, DateTimeOffset.UtcNow.AddMinutes(30)));
        _host = await TestHost.StartAsync(services => services.AddSingleton(_uploads).AddSingleton(_store));
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private async Task<string> BeginAsync(string prefix = "/v1.0/me")
    {
        var response = await _host.AuthorizedClient().PostAsync($"{prefix}/events/{Event}/attachments/createUploadSession",
            new StringContent("""{"AttachmentItem":{"attachmentType":"file","name":"big.bin","size":10,"contentType":"application/pdf"}}""", Encoding.UTF8, "application/json"), Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(Ct));
        var body = await response.ReadObjectAsync();
        body["nextExpectedRanges"]!.AsArray().Single()!.GetValue<string>().ShouldBe("0-");
        return body["uploadUrl"]!.GetValue<string>();
    }

    private Task<HttpResponseMessage> PutAsync(string uploadUrl, int first, int length, int total = 10)
    {
        var content = new ByteArrayContent(new byte[length]);
        content.Headers.ContentRange = new ContentRangeHeaderValue(first, first + length - 1, total);
        return _host.Client.PutAsync(uploadUrl[TestHost.GraphBase.Length..].Insert(0, "/v1.0"), content, Ct);
    }

    [Theory]
    [InlineData("/v1.0/me", TestHost.UserEmail)]
    [InlineData("/v1.0/users/shared@example.com", "shared@example.com")]
    public async Task The_upload_url_names_the_event_and_session_on_the_graph_base(string prefix, string mailbox)
    {
        var url = await BeginAsync(prefix);

        url.ShouldStartWith($"{TestHost.GraphBase}/users/{mailbox}/events/{Event}/attachmentSessions/s1/content?authtoken=");
        await _uploads.Received(1).BeginAsync(new MailboxRef(mailbox), new EventId(Event), File, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_middle_chunk_answers_the_next_byte_and_the_last_201_with_a_location()
    {
        var url = await BeginAsync();
        _uploads.AppendAsync(Me, new EventId(Event), "s1", 0, 4, 10, Arg.Any<Stream>(), Arg.Any<CancellationToken>()).Returns(new EventUploadProgress(4, null));
        _uploads.AppendAsync(Me, new EventId(Event), "s1", 4, 6, 10, Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new EventUploadProgress(10, new EventAttachment(new AttachmentId("a1"), "big.bin", "application/pdf", 10)));

        var middle = await PutAsync(url, 0, 4);
        middle.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await middle.ReadObjectAsync())["nextExpectedRanges"]!.AsArray().Single()!.GetValue<string>().ShouldBe("4");

        var last = await PutAsync(url, 4, 6);
        last.StatusCode.ShouldBe(HttpStatusCode.Created);
        last.Headers.Location!.ToString().ShouldBe($"{TestHost.GraphBase}/users('me%40example.com')/events('{Event}')/attachments('a1')");
    }

    [Fact]
    public async Task A_chunk_without_a_range_is_400_and_a_forged_url_401()
    {
        var url = await BeginAsync();

        (await _host.Client.PutAsync(url[TestHost.GraphBase.Length..].Insert(0, "/v1.0"), new ByteArrayContent([1]), Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var forged = url[..^4] + (url[^4] == 'A' ? "B" : "A") + url[^3..];
        (await PutAsync(forged, 0, 4)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        _uploads.ReceivedCalls().ShouldNotContain(c => c.GetMethodInfo().Name == nameof(IEventAttachmentUploads.AppendAsync));
    }

    [Theory]
    [InlineData("""{"AttachmentItem":{"attachmentType":"reference","name":"x","size":10}}""", HttpStatusCode.NotImplemented)]
    [InlineData("""{"AttachmentItem":{"attachmentType":"file","name":"x","size":10,"isInline":true}}""", HttpStatusCode.NotImplemented)]
    [InlineData("""{"AttachmentItem":{"attachmentType":"file","name":" ","size":10}}""", HttpStatusCode.BadRequest)]
    [InlineData("{}", HttpStatusCode.BadRequest)]
    public async Task A_session_that_isnt_a_named_file_is_refused(string body, HttpStatusCode expected)
    {
        var response = await _host.AuthorizedClient().PostAsync($"/v1.0/me/events/{Event}/attachments/createUploadSession",
            new StringContent(body, Encoding.UTF8, "application/json"), Ct);

        response.StatusCode.ShouldBe(expected);
    }

    [Fact]
    public async Task Value_streams_the_file_with_its_own_type()
    {
        _store.GetContentAsync(Me, new EventId(Event), new AttachmentId("a1"), Arg.Any<CancellationToken>())
            .Returns(new BlobContent("application/pdf", 3, _ => Task.FromResult<Stream>(new MemoryStream([1, 2, 3]))));

        var response = await _host.AuthorizedClient().GetAsync($"/v1.0/me/events/{Event}/attachments/a1/$value", Ct);

        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
        (await response.Content.ReadAsByteArrayAsync(Ct)).ShouldBe([1, 2, 3]);
    }

    [Fact]
    public async Task Value_of_an_unknown_attachment_is_404()
    {
        _store.GetContentAsync(default, default!, default!, Ct).ReturnsForAnyArgs(Result<BlobContent>.Failure(Error.NotFound("gone")));

        (await _host.AuthorizedClient().GetAsync($"/v1.0/me/events/{Event}/attachments/zz/$value", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}

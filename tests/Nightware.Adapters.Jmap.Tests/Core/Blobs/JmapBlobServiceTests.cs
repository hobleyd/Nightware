using System.Text;
using Nightware.Adapters.Jmap.Tests.Support;
using Nightware.Application.Common.Identity;
using Nightware.Domain.Kernel;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Nightware.Adapters.Jmap.Tests.Core.Blobs;

public sealed class JmapBlobServiceTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Upload_posts_the_stream_to_the_account_upload_url()
    {
        await using var host = new JmapTestHost();
        host.StubSession();
        host.Server.Given(Request.Create().WithPath("/jmap/upload/c/").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithHeader("Content-Type", "application/json")
                .WithBody("""{"accountId":"c","blobId":"Gb5f55i6","type":"message/rfc822","size":11}"""));
        using var content = new MemoryStream(Encoding.ASCII.GetBytes("Subject: x\n"));

        var blob = await host.Blobs().UploadAsync("c", content, "message/rfc822", Ct);

        blob.Value.ShouldBe(new Nightware.Adapters.Jmap.Core.Blobs.UploadedBlob("c", "Gb5f55i6", "message/rfc822", 11));
        var request = host.Requests("/jmap/upload/c/", "POST").ShouldHaveSingleItem().RequestMessage!;
        request.Body.ShouldBe("Subject: x\n");
        request.Headers!["Content-Type"].First().ShouldStartWith("message/rfc822");
        request.Headers["Authorization"].First().ShouldBe("Bearer alice-token");
    }

    [Fact]
    public async Task Upload_escapes_the_account_id()
    {
        await using var host = new JmapTestHost();
        host.StubSession();
        host.Server.Given(Request.Create().UsingPost())
            .RespondWith(Response.Create().WithStatusCode(201).WithBody("""{"accountId":"a b","blobId":"B","type":"text/plain","size":1}"""));

        (await host.Blobs().UploadAsync("a b", new MemoryStream([1]), "text/plain", Ct)).IsSuccess.ShouldBeTrue();

        host.Requests("/jmap/upload/a b/", "POST").ShouldHaveSingleItem();
        (await host.Client().GetSessionAsync(Ct)).Value.UploadUrl("a/b").AbsoluteUri.ShouldEndWith("/jmap/upload/a%2Fb/");
    }

    [Fact]
    public async Task Upload_rejects_streams_larger_than_the_server_limit_before_sending()
    {
        await using var host = new JmapTestHost();
        host.StubSession(JmapTestHost.Session().Replace("\"maxSizeUpload\":50000000", "\"maxSizeUpload\":4", StringComparison.Ordinal));

        var result = await host.Blobs().UploadAsync("c", new MemoryStream(new byte[5]), "application/octet-stream", Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.PayloadTooLarge);
        host.Requests("/jmap/upload/c/", "POST").ShouldBeEmpty();
    }

    [Theory]
    [InlineData(413, ErrorKind.PayloadTooLarge)]
    [InlineData(401, ErrorKind.BackendUnavailable)] // the host's credential is a token exchange (ADR 0001 §42)
    [InlineData(403, ErrorKind.Forbidden)]
    [InlineData(503, ErrorKind.BackendUnavailable)]
    public async Task Upload_failures_are_mapped(int status, ErrorKind expected)
    {
        await using var host = new JmapTestHost();
        host.StubSession();
        host.Server.Given(Request.Create().WithPath("/jmap/upload/c/").UsingPost()).RespondWith(Response.Create().WithStatusCode(status));

        (await host.Blobs().UploadAsync("c", new MemoryStream([1]), "text/plain", Ct)).Error!.Kind.ShouldBe(expected);
    }

    [Fact]
    public async Task Upload_with_an_unreadable_response_is_unexpected()
    {
        await using var host = new JmapTestHost();
        host.StubSession();
        host.Server.Given(Request.Create().WithPath("/jmap/upload/c/").UsingPost()).RespondWith(Response.Create().WithStatusCode(200).WithBody("<html/>"));

        (await host.Blobs().UploadAsync("c", new MemoryStream([1]), "text/plain", Ct)).Error!.Kind.ShouldBe(ErrorKind.Unknown);
    }

    [Fact]
    public async Task Upload_is_never_retried()
    {
        await using var host = new JmapTestHost(s => s["Jmap:MaxRetryAttempts"] = "3");
        host.StubSession();
        host.Server.Given(Request.Create().WithPath("/jmap/upload/c/").UsingPost()).RespondWith(Response.Create().WithStatusCode(503));

        await host.Blobs().UploadAsync("c", new MemoryStream([1]), "text/plain", Ct);

        host.Requests("/jmap/upload/c/", "POST").Count.ShouldBe(1);
    }

    [Fact]
    public async Task Download_streams_the_blob_from_the_template_url()
    {
        await using var host = new JmapTestHost();
        host.StubSession();
        var payload = new byte[256 * 1024];
        Random.Shared.NextBytes(payload);
        host.Server.Given(Request.Create().WithPath("/jmap/download/c/G2/Q3 report.pdf").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/pdf").WithBody(payload));

        var blob = (await host.Blobs().DownloadAsync("c", "G2", "Q3 report.pdf", "application/pdf", Ct)).Value;

        blob.ContentType.ShouldBe("application/pdf");
        // WireMock streams bodies chunked; when the server sends Content-Length it becomes Length.
        blob.Length.ShouldBeOneOf(payload.Length, null);
        blob.FileName.ShouldBe("Q3 report.pdf");
        await using (var stream = await blob.Open(Ct))
        {
            stream.CanSeek.ShouldBeFalse();
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy, Ct);
            copy.ToArray().ShouldBe(payload);
        }

        var request = host.Requests("/jmap/download/c/G2/Q3 report.pdf", "GET").ShouldHaveSingleItem().RequestMessage!;
        request.Url.ShouldEndWith("/Q3 report.pdf?accept=application%2Fpdf");
        request.Headers!["Authorization"].First().ShouldBe("Bearer alice-token");
    }

    [Fact]
    public async Task Download_can_be_opened_again()
    {
        await using var host = new JmapTestHost();
        host.StubSession();
        host.Server.Given(Request.Create().WithPath("/jmap/download/c/G2/blob").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("hello"));

        var blob = (await host.Blobs().DownloadAsync("c", "G2", null, "text/plain", Ct)).Value;

        foreach (var _ in Enumerable.Range(0, 2))
        {
            await using var stream = await blob.Open(Ct);
            using var reader = new StreamReader(stream);
            (await reader.ReadToEndAsync(Ct)).ShouldBe("hello");
        }

        host.Requests("/jmap/download/c/G2/blob", "GET").Count.ShouldBe(2);
        blob.FileName.ShouldBeNull();
    }

    [Fact]
    public async Task A_failing_reopen_throws_io_exception()
    {
        await using var host = new JmapTestHost();
        host.StubSession();
        host.Server.Given(Request.Create().WithPath("/jmap/download/c/G2/blob").UsingGet())
            .InScenario("once").WillSetStateTo("gone")
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("x"));
        host.Server.Given(Request.Create().WithPath("/jmap/download/c/G2/blob").UsingGet())
            .InScenario("once").WhenStateIs("gone")
            .RespondWith(Response.Create().WithStatusCode(404));
        var blob = (await host.Blobs().DownloadAsync("c", "G2", null, "text/plain", Ct)).Value;
        await (await blob.Open(Ct)).DisposeAsync();

        await Should.ThrowAsync<IOException>(() => blob.Open(Ct));
    }

    [Theory]
    [InlineData(404, ErrorKind.NotFound)]
    [InlineData(403, ErrorKind.Forbidden)]
    [InlineData(401, ErrorKind.BackendUnavailable)] // the host's credential is a token exchange (ADR 0001 §42)
    [InlineData(500, ErrorKind.BackendUnavailable)]
    public async Task Download_failures_surface_before_streaming(int status, ErrorKind expected)
    {
        await using var host = new JmapTestHost();
        host.StubSession();
        host.Server.Given(Request.Create().WithPath("/jmap/download/c/missing/blob").UsingGet()).RespondWith(Response.Create().WithStatusCode(status));

        (await host.Blobs().DownloadAsync("c", "missing", null, "text/plain", Ct)).Error!.Kind.ShouldBe(expected);
    }

    [Fact]
    public async Task Downloads_are_retried_on_transient_failures()
    {
        await using var host = new JmapTestHost(s => s["Jmap:MaxRetryAttempts"] = "1");
        host.StubSession();
        host.Server.Given(Request.Create().WithPath("/jmap/download/c/G/blob").UsingGet())
            .InScenario("flaky").WillSetStateTo("ok").RespondWith(Response.Create().WithStatusCode(502));
        host.Server.Given(Request.Create().WithPath("/jmap/download/c/G/blob").UsingGet())
            .InScenario("flaky").WhenStateIs("ok").RespondWith(Response.Create().WithStatusCode(200).WithBody("ok"));

        (await host.Blobs().DownloadAsync("c", "G", null, "text/plain", Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task A_401_on_a_passed_through_token_asks_the_client_to_re_authenticate()
    {
        await using var host = new JmapTestHost();
        host.UseCredential(new BackendCredential("Bearer", "user-token", CredentialStrategy.PassThrough));
        host.StubSession();
        host.Server.Given(Request.Create().WithPath("/jmap/upload/c/").UsingPost()).RespondWith(Response.Create().WithStatusCode(401));
        host.Server.Given(Request.Create().WithPath("/jmap/download/c/b/blob").UsingGet()).RespondWith(Response.Create().WithStatusCode(401));

        (await host.Blobs().UploadAsync("c", new MemoryStream([1]), "text/plain", Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendAuthFailed);
        (await host.Blobs().DownloadAsync("c", "b", null, "text/plain", Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendAuthFailed);
    }

    [Fact]
    public async Task Blob_operations_return_connection_errors()
    {
        await using var host = new JmapTestHost();
        host.UseCredential(new BackendCredential("Bearer", "user-token", CredentialStrategy.PassThrough));
        host.Server.Given(Request.Create().WithPath("/jmap/session").UsingGet()).RespondWith(Response.Create().WithStatusCode(401));

        (await host.Blobs().UploadAsync("c", new MemoryStream([1]), "text/plain", Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendAuthFailed);
        (await host.Blobs().DownloadAsync("c", "b", null, "text/plain", Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendAuthFailed);
    }
}

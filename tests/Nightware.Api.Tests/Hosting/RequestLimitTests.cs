using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Nightware.Api.Hosting;
using Nightware.Api.Tests.Support;

namespace Nightware.Api.Tests.Hosting;

/// <summary>Body-size and Kestrel limits: oversize requests get Graph's 413 envelope, never a bare reset.</summary>
public sealed class RequestLimitTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Dictionary<string, string?> SmallLimit = new() { ["Limits:MaxRequestBodyBytes"] = "1000" };

    private static void Upload(IServiceCollection services, StrongBox<bool> reached) =>
        services.AddGraphProbes(g => g.MapPost("/upload", async (HttpRequest request) =>
        {
            reached.Value = true;
            await request.Body.CopyToAsync(Stream.Null, request.HttpContext.RequestAborted);
            return Results.Ok();
        }));

    [Fact]
    public async Task A_declared_oversize_body_is_refused_before_the_endpoint_runs()
    {
        var reached = new StrongBox<bool>();
        await using var host = await TestHost.StartAsync(s => Upload(s, reached), configuration: SmallLimit);

        var response = await host.AuthorizedClient().PostAsync("/v1.0/upload", new ByteArrayContent(new byte[1001]), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        var body = await response.ReadObjectAsync();
        body.Str("error.code").ShouldBe("requestEntityTooLarge");
        body.Str("error.message")!.ShouldContain("1000 bytes");
        reached.Value.ShouldBeFalse();
    }

    [Fact]
    public async Task A_body_at_the_limit_is_accepted()
    {
        var reached = new StrongBox<bool>();
        await using var host = await TestHost.StartAsync(s => Upload(s, reached), configuration: SmallLimit);

        var response = await host.AuthorizedClient().PostAsync("/v1.0/upload", new ByteArrayContent(new byte[1000]), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_chunked_oversize_body_is_a_graph_413_on_kestrel()
    {
        var reached = new StrongBox<bool>();
        await using var host = await KestrelTestHost.StartAsync(SmallLimit, s => Upload(s, reached));
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1.0/upload")
        {
            // A non-seekable stream has no length, so the body goes out chunked: only Kestrel can count it.
            Content = new StreamContent(new NonSeekableStream(new byte[5000])),
        };
        request.Headers.Authorization = new("Bearer", TestHost.Token());

        var response = await host.Client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await response.ReadObjectAsync()).Str("error.code").ShouldBe("requestEntityTooLarge");
        reached.Value.ShouldBeTrue();
    }

    [Fact]
    public async Task Kestrel_limits_come_from_configuration()
    {
        await using var host = await KestrelTestHost.StartAsync(new Dictionary<string, string?> { ["Kestrel:Limits:MaxRequestHeadersTotalSize"] = "4096" });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/.well-known/nightware");
        request.Headers.Add("X-Padding", new string('a', 8000));

        var response = await host.Client.SendAsync(request, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestHeaderFieldsTooLarge);
    }

    [Fact]
    public async Task Kestrel_does_not_announce_itself()
    {
        await using var host = await KestrelTestHost.StartAsync();

        var response = await host.Client.GetAsync("/.well-known/nightware", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.Server.ShouldBeEmpty();
    }

    [Fact]
    public void The_default_limit_admits_a_base64_attachment_as_large_as_stalwart_accepts()
    {
        var limit = new RequestLimitOptions().MaxRequestBodyBytes;

        limit.ShouldBeGreaterThan(RequestLimitOptions.StalwartMaxSizeUpload * 4 / 3);
        limit.ShouldBeLessThan(RequestLimitOptions.StalwartMaxSizeUpload * 2);
    }

    [Fact]
    public void A_non_positive_limit_is_rejected() =>
        new RequestLimitOptionsValidator().Validate(null, new RequestLimitOptions { MaxRequestBodyBytes = 0 }).Failed.ShouldBeTrue();

    private sealed class StrongBox<T>
    {
        public T? Value { get; set; }
    }

    private sealed class NonSeekableStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

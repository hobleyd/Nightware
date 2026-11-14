using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

using Nightware.Api.Graph;
using Nightware.Api.Graph.Errors;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Tests.Graph.Errors;

public sealed class GraphErrorWriterTests
{
    private static DefaultHttpContext Context(string? clientRequestId = null)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 15, 1, 2, 3, TimeSpan.Zero));
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton<TimeProvider>(clock).BuildServiceProvider(),
        };
        context.Response.Body = new MemoryStream();
        if (clientRequestId is not null)
        {
            context.Request.Headers[RequestCorrelation.ClientRequestIdHeader] = clientRequestId;
        }

        return context;
    }

    [Fact]
    public async Task Writes_the_graph_envelope_with_correlation()
    {
        var context = Context("abc-123");

        await GraphErrorWriter.WriteAsync(context, Error.NotFound("The specified object was not found in the store."));

        context.Response.StatusCode.ShouldBe(404);
        context.Response.ContentType.ShouldBe("application/json");
        context.Response.Body.Position = 0;
        var body = JsonNode.Parse(context.Response.Body)!;
        var correlation = RequestCorrelation.Get(context);
        var expected = new JsonObject
        {
            ["error"] = new JsonObject
            {
                ["code"] = "ErrorItemNotFound",
                ["message"] = "The specified object was not found in the store.",
                ["innerError"] = new JsonObject
                {
                    ["date"] = "2026-11-15T01:02:03",
                    ["request-id"] = correlation.RequestId,
                    ["client-request-id"] = "abc-123",
                },
            },
        };
        JsonNode.DeepEquals(body, expected).ShouldBeTrue(body.ToJsonString());
    }

    [Fact]
    public async Task Client_request_id_defaults_to_the_request_id_and_retry_after_is_integer_seconds()
    {
        var context = Context();

        await GraphErrorWriter.WriteAsync(context, new Error(ErrorKind.Throttled, "x", "slow down") { RetryAfterSeconds = 12 });

        context.Response.StatusCode.ShouldBe(429);
        context.Response.Headers.RetryAfter.ToString().ShouldBe("12");
        var envelope = GraphErrorWriter.Envelope(context, GraphErrorMapper.Map(Error.Validation("x")));
        envelope["error"]!["innerError"]!["client-request-id"]!.GetValue<string>().ShouldBe(RequestCorrelation.Get(context).RequestId);
    }

    [Fact]
    public void Works_without_request_services()
    {
        var envelope = GraphErrorWriter.Envelope(new DefaultHttpContext(), GraphErrorMapper.Map(Error.Validation("bad")));

        envelope["error"]!["code"]!.GetValue<string>().ShouldBe("invalidRequest");
    }

    [Theory]
    [InlineData("")]
    [InlineData("has\nnewline")]
    public void Invalid_client_request_ids_are_not_echoed(string value)
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[RequestCorrelation.ClientRequestIdHeader] = value;

        RequestCorrelation.Get(context).ClientRequestId.ShouldBeNull();
    }

    [Fact]
    public void Overlong_client_request_ids_are_not_echoed()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers[RequestCorrelation.ClientRequestIdHeader] = new string('a', RequestCorrelation.MaxClientRequestIdLength + 1);

        RequestCorrelation.Get(context).ClientRequestId.ShouldBeNull();
    }

    [Fact]
    public void Correlation_is_stable_within_a_request()
    {
        var context = new DefaultHttpContext();

        RequestCorrelation.Get(context).ShouldBeSameAs(RequestCorrelation.Get(context));
        Guid.TryParse(RequestCorrelation.Get(context).RequestId, out _).ShouldBeTrue();
    }

    [Theory]
    [InlineData("/v1.0/me/foo/bar", "bar")]
    [InlineData("/beta", "beta")]
    [InlineData("", "")]
    public void Unknown_route_names_the_last_segment(string path, string segment)
    {
        UnknownGraphRoute.LastSegment(new PathString(path)).ShouldBe(segment);
    }

    [Theory]
    [InlineData(400, "BadRequest")]
    [InlineData(401, "InvalidAuthenticationToken")]
    [InlineData(403, "ErrorAccessDenied")]
    [InlineData(404, "ResourceNotFound")]
    [InlineData(405, "Request_BadRequest")]
    [InlineData(413, "requestEntityTooLarge")]
    [InlineData(415, "UnsupportedMediaType")]
    [InlineData(502, "generalException")]
    [InlineData(418, "BadRequest")]
    public void Framework_statuses_get_graph_codes(int status, string code)
    {
        GraphExceptionMiddleware.CodeForStatus(status).ShouldBe(code);
    }
}

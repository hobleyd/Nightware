using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

using Nightware.Api.Graph;
using Nightware.Api.Graph.Batch;
using Nightware.Api.Graph.OData;
using Nightware.Api.Graph.Routing;
using Nightware.Api.Tests.Support;

namespace Nightware.Api.Tests.Graph.Batch;

/// <summary>
/// <c>POST /$batch</c> end to end: <see cref="BatchEndpoints"/>, <see cref="BatchDispatcher"/>,
/// <see cref="BatchSubRequest"/> and <see cref="GraphBatchPipeline"/> over the real pipeline.
/// </summary>
public sealed class BatchEndpointTests : IAsyncLifetime
{
    private readonly ConcurrencyProbe _concurrency = new();
    private TestHost _host = null!;

    public async ValueTask InitializeAsync() =>
        _host = await TestHost.StartAsync(services => services
            .AddSingleton(_concurrency)
            .AddGraphProbes(static graph =>
            {
                foreach (var scope in graph.MapMailboxGroups())
                {
                    scope.MapGet("/batchProbe", static (Mailbox mailbox, ODataQuery odata, PreferHeader prefer, HttpContext http) =>
                    {
                        prefer.MarkTimeZoneApplied(http.Response);
                        return Results.Json(new { mailbox = mailbox.Ref.Address, top = odata.Query.Top, path = http.Request.Path.Value });
                    });
                }

                graph.MapGet("/batchPage", static (ODataQuery odata) =>
                    GraphResults.Collection([new { id = "a" }], "probe", odata.Query, "next-page"));
                graph.MapPost("/batchEcho", static (JsonObject body) => Results.Json(body, statusCode: StatusCodes.Status201Created));
                graph.MapPost("/batchRaw", static async (HttpContext http) =>
                {
                    using var reader = new StreamReader(http.Request.Body);
                    return Results.Text($"{http.Request.ContentType}|{await reader.ReadToEndAsync()}");
                });
                graph.MapGet("/batchBytes", static () => Results.Bytes([1, 2, 3], "application/octet-stream"));
                graph.MapDelete("/batchNothing", static () => Results.NoContent());
                graph.MapGet("/batchSlow", static async (ConcurrencyProbe probe) =>
                {
                    await probe.EnterAsync();
                    return Results.Json(new { ok = true });
                });
            }));

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonObject Request(string id, string url, string method = "GET", JsonObject? headers = null, JsonNode? body = null, params string[] dependsOn)
    {
        var request = new JsonObject { ["id"] = id, ["method"] = method, ["url"] = url };
        if (headers is not null)
        {
            request["headers"] = headers;
        }

        if (body is not null)
        {
            request["body"] = body;
        }

        if (dependsOn.Length > 0)
        {
            request["dependsOn"] = new JsonArray([.. dependsOn.Select(static d => (JsonNode)d)]);
        }

        return request;
    }

    private async Task<JsonObject> PostBatchAsync(string version, params JsonObject[] requests)
    {
        var response = await _host.AuthorizedClient().PostAsJsonAsync(
            $"{version}/$batch", new JsonObject { ["requests"] = new JsonArray(requests) }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/json");
        return await response.ReadObjectAsync();
    }

    private static JsonObject ResponseFor(JsonObject batch, string id) =>
        batch["responses"]!.AsArray().Single(r => r!["id"]!.GetValue<string>() == id)!.AsObject();

    private static int Status(JsonObject response) => response["status"]!.GetValue<int>();

    [Fact]
    public async Task A_batched_request_answers_as_the_direct_call_does()
    {
        var direct = await (await _host.AuthorizedClient().GetAsync("/v1.0/me/batchProbe?$top=5", Ct)).ReadObjectAsync();

        var batch = await PostBatchAsync("/v1.0", Request("1", "/me/batchProbe?$top=5"));

        var response = ResponseFor(batch, "1");
        Status(response).ShouldBe(200);
        JsonNode.DeepEquals(response["body"], direct).ShouldBeTrue();
        direct["mailbox"]!.GetValue<string>().ShouldBe(TestHost.UserEmail);
        direct["top"]!.GetValue<int>().ShouldBe(5);
    }

    [Fact]
    public async Task A_url_without_a_leading_slash_resolves_under_the_version_root()
    {
        var batch = await PostBatchAsync("/beta", Request("1", "me/batchProbe"));

        ResponseFor(batch, "1")["body"]!["path"]!.GetValue<string>().ShouldBe("/beta/me/batchProbe");
    }

    [Fact]
    public async Task Each_request_carries_its_own_headers()
    {
        var batch = await PostBatchAsync("/v1.0", Request("1", "/me/batchProbe",
            headers: new JsonObject { ["Prefer"] = "outlook.timezone=\"UTC\"" }));

        ResponseFor(batch, "1")["headers"]!["Preference-Applied"]!.GetValue<string>().ShouldContain("outlook.timezone");
    }

    [Fact]
    public async Task One_failing_request_does_not_fail_its_siblings()
    {
        var batch = await PostBatchAsync("/v1.0",
            Request("1", "/me/batchProbe"),
            Request("2", "/me/no-such-route"));

        Status(ResponseFor(batch, "1")).ShouldBe(200);
        var failed = ResponseFor(batch, "2");
        Status(failed).ShouldBe(400);
        failed["body"].Str("error.code").ShouldBe("BadRequest");
    }

    [Fact]
    public async Task Responses_come_back_in_request_order()
    {
        var batch = await PostBatchAsync("/v1.0",
            Request("b", "/me/batchProbe"), Request("a", "/batchBytes"), Request("c", "/batchPage"));

        batch["responses"]!.AsArray().Select(static r => r!["id"]!.GetValue<string>()).ShouldBe(["b", "a", "c"]);
    }

    [Fact]
    public async Task A_dependant_of_a_failed_request_is_answered_424_without_running()
    {
        var batch = await PostBatchAsync("/v1.0",
            Request("1", "/me/no-such-route"),
            Request("2", "/batchEcho", "POST", new JsonObject { ["Content-Type"] = "application/json" }, new JsonObject { ["x"] = 1 }, "1"),
            Request("3", "/batchSlow", dependsOn: "2"));

        Status(ResponseFor(batch, "1")).ShouldBe(400);
        Status(ResponseFor(batch, "2")).ShouldBe(424);
        ResponseFor(batch, "2")["body"].Str("error.code").ShouldBe(BatchDispatcher.FailedDependencyCode);
        Status(ResponseFor(batch, "3")).ShouldBe(424);
        _concurrency.Entered.ShouldBe(0);
    }

    [Fact]
    public async Task A_dependant_runs_after_what_it_depends_on()
    {
        var batch = await PostBatchAsync("/v1.0",
            Request("2", "/batchSlow", dependsOn: "1"),
            Request("1", "/batchSlow"));

        Status(ResponseFor(batch, "1")).ShouldBe(200);
        Status(ResponseFor(batch, "2")).ShouldBe(200);
        _concurrency.MaxInFlight.ShouldBe(1);
    }

    [Fact]
    public async Task At_most_four_requests_run_at_once()
    {
        var requests = Enumerable.Range(1, BatchRequestReader.MaxRequests)
            .Select(static i => Request(i.ToString(System.Globalization.CultureInfo.InvariantCulture), "/batchSlow"))
            .ToArray();

        var batch = await PostBatchAsync("/v1.0", requests);

        batch["responses"]!.AsArray().ShouldAllBe(r => r!["status"]!.GetValue<int>() == 200);
        _concurrency.Entered.ShouldBe(BatchRequestReader.MaxRequests);
        _concurrency.MaxInFlight.ShouldBeInRange(2, BatchDispatcher.MaxConcurrency);
    }

    [Fact]
    public async Task A_json_body_is_sent_and_a_created_status_kept()
    {
        var batch = await PostBatchAsync("/v1.0", Request("1", "/batchEcho", "POST",
            new JsonObject { ["Content-Type"] = "application/json" }, new JsonObject { ["displayName"] = "Folder" }));

        var response = ResponseFor(batch, "1");
        Status(response).ShouldBe(201);
        response["body"].Str("displayName").ShouldBe("Folder");
    }

    [Fact]
    public async Task Text_bodies_are_sent_as_text_and_others_decoded_from_base64()
    {
        var batch = await PostBatchAsync("/v1.0",
            Request("text", "/batchRaw", "POST", new JsonObject { ["Content-Type"] = "text/plain" }, "hello"),
            Request("binary", "/batchRaw", "POST", new JsonObject { ["Content-Type"] = "application/octet-stream" },
                Convert.ToBase64String(Encoding.UTF8.GetBytes("bytes"))));

        ResponseFor(batch, "text")["body"]!.GetValue<string>().ShouldBe("text/plain|hello");
        ResponseFor(batch, "binary")["body"]!.GetValue<string>().ShouldBe("application/octet-stream|bytes");
    }

    [Fact]
    public async Task A_binary_response_is_base64_and_no_content_has_no_body()
    {
        var batch = await PostBatchAsync("/v1.0", Request("1", "/batchBytes"), Request("2", "/batchNothing", "DELETE"));

        ResponseFor(batch, "1")["body"]!.GetValue<string>().ShouldBe(Convert.ToBase64String([1, 2, 3]));
        Status(ResponseFor(batch, "2")).ShouldBe(204);
        ResponseFor(batch, "2").ContainsKey("body").ShouldBeFalse();
    }

    [Fact]
    public async Task Continuation_links_are_absolute_on_the_graph_base()
    {
        var batch = await PostBatchAsync("/v1.0", Request("1", "/batchPage?$top=1"));

        ResponseFor(batch, "1")["body"]!["@odata.nextLink"]!.GetValue<string>().ShouldStartWith(TestHost.GraphBase + "/batchPage?");
    }

    [Theory]
    [InlineData("https://graph.microsoft.com/v1.0/me", "relative")]
    [InlineData("/$batch", "$batch")]
    [InlineData("/batchPage?authtoken=abc", "self-authorising")]
    public async Task Refused_urls_are_answered_by_that_request_alone(string url, string message)
    {
        var batch = await PostBatchAsync("/v1.0", Request("1", url), Request("2", "/me/batchProbe"));

        var refused = ResponseFor(batch, "1");
        Status(refused).ShouldBe(400);
        refused["body"].Str("error.message")!.ShouldContain(message);
        Status(ResponseFor(batch, "2")).ShouldBe(200);
    }

    [Fact]
    public async Task A_body_without_a_content_type_is_refused_for_that_request()
    {
        var batch = await PostBatchAsync("/v1.0", Request("1", "/batchEcho", "POST", body: new JsonObject { ["x"] = 1 }));

        Status(ResponseFor(batch, "1")).ShouldBe(400);
        ResponseFor(batch, "1")["body"].Str("error.message")!.ShouldContain("Content-Type");
    }

    [Fact]
    public async Task An_invalid_batch_is_refused_whole()
    {
        var response = await _host.AuthorizedClient().PostAsJsonAsync(
            "/v1.0/$batch", new JsonObject { ["requests"] = new JsonArray(Request("1", "/me"), Request("1", "/me")) }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.ReadObjectAsync()).Str("error.code").ShouldBe("invalidRequest");
    }

    [Fact]
    public async Task An_unauthenticated_batch_is_refused_whole()
    {
        var response = await _host.Client.PostAsJsonAsync(
            "/v1.0/$batch", new JsonObject { ["requests"] = new JsonArray(Request("1", "/me/batchProbe")) }, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public void The_pipeline_must_be_built_before_use() =>
        Should.Throw<InvalidOperationException>(() => new GraphBatchPipeline().Pipeline);

    /// <summary>Counts concurrent entries into <c>/batchSlow</c>.</summary>
    internal sealed class ConcurrencyProbe
    {
        private int _inFlight;
        private int _maxInFlight;
        private int _entered;

        public int MaxInFlight => Volatile.Read(ref _maxInFlight);

        public int Entered => Volatile.Read(ref _entered);

        public async Task EnterAsync()
        {
            Interlocked.Increment(ref _entered);
            var now = Interlocked.Increment(ref _inFlight);
            int seen;
            while (now > (seen = Volatile.Read(ref _maxInFlight)) && Interlocked.CompareExchange(ref _maxInFlight, now, seen) != seen)
            {
            }

            await Task.Delay(50);
            Interlocked.Decrement(ref _inFlight);
        }
    }
}

using System.Text.Json.Nodes;

using Nightware.Api.Graph.Batch;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Tests.Graph.Batch;

public sealed class BatchRequestReaderTests
{
    private static JsonObject Batch(params JsonNode[] requests) => new() { ["requests"] = new JsonArray(requests) };

    private static JsonObject Get(string id, string url = "/me", params string[] dependsOn)
    {
        var request = new JsonObject { ["id"] = id, ["method"] = "GET", ["url"] = url };
        if (dependsOn.Length > 0)
        {
            request["dependsOn"] = new JsonArray([.. dependsOn.Select(static d => (JsonNode)d)]);
        }

        return request;
    }

    private static string Failure(JsonObject? body)
    {
        var result = BatchRequestReader.Read(body);
        result.IsSuccess.ShouldBeFalse();
        result.Error.Kind.ShouldBe(ErrorKind.Validation);
        return result.Error.Message;
    }

    [Fact]
    public void Reads_each_request_with_its_headers_body_and_dependencies()
    {
        var body = new JsonObject
        {
            ["Requests"] = new JsonArray(
                Get("1"),
                new JsonObject
                {
                    ["ID"] = "2",
                    ["Method"] = "post",
                    ["URL"] = "me/mailFolders",
                    ["DependsOn"] = new JsonArray("1"),
                    ["Headers"] = new JsonObject { ["Content-Type"] = "application/json" },
                    ["Body"] = new JsonObject { ["displayName"] = "x" },
                }),
        };

        var items = BatchRequestReader.Read(body);

        items.IsSuccess.ShouldBeTrue();
        items.Value.Count.ShouldBe(2);
        var second = items.Value[1];
        second.Id.ShouldBe("2");
        second.Method.ShouldBe("POST");
        second.Url.ShouldBe("me/mailFolders");
        second.DependsOn.ShouldBe(["1"]);
        second.Headers["content-type"].ShouldBe("application/json");
        second.Body!["displayName"]!.GetValue<string>().ShouldBe("x");
    }

    [Fact]
    public void A_body_without_requests_is_refused() => Failure(new JsonObject()).ShouldContain("'requests'");

    [Fact]
    public void An_empty_batch_is_refused() => Failure(Batch()).ShouldContain("at least one");

    [Fact]
    public void More_than_twenty_requests_are_refused()
    {
        var requests = Enumerable.Range(1, BatchRequestReader.MaxRequests + 1).Select(static i => (JsonNode)Get(i.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        Failure(Batch([.. requests])).ShouldContain("too large");
    }

    [Fact]
    public void Exactly_twenty_requests_are_accepted()
    {
        var requests = Enumerable.Range(1, BatchRequestReader.MaxRequests).Select(static i => (JsonNode)Get(i.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        BatchRequestReader.Read(Batch([.. requests])).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_request_without_an_id_method_or_url_is_refused()
    {
        Failure(Batch(new JsonObject { ["method"] = "GET", ["url"] = "/me" })).ShouldContain("'id'");
        Failure(Batch(new JsonObject { ["id"] = "1", ["url"] = "/me" })).ShouldContain("'method'");
        Failure(Batch(new JsonObject { ["id"] = "1", ["method"] = "GET" })).ShouldContain("'url'");
        Failure(Batch(JsonValue.Create("GET /me")!)).ShouldContain("JSON object");
    }

    [Fact]
    public void Duplicate_ids_are_refused() => Failure(Batch(Get("1"), Get("1"))).ShouldContain("more than once");

    [Fact]
    public void A_dependency_outside_the_batch_is_refused() => Failure(Batch(Get("1", "/me", "9"))).ShouldContain("'9'");

    [Fact]
    public void A_request_depending_on_itself_is_refused() => Failure(Batch(Get("1", "/me", "1"))).ShouldContain("itself");

    [Fact]
    public void A_dependency_cycle_is_refused() =>
        Failure(Batch(Get("1", "/me", "3"), Get("2", "/me", "1"), Get("3", "/me", "2"))).ShouldContain("cycle");

    [Fact]
    public void Malformed_depends_on_and_headers_are_refused()
    {
        var dependsOn = Get("1");
        dependsOn["dependsOn"] = "2";
        Failure(Batch(dependsOn)).ShouldContain("dependsOn");

        var headers = Get("1");
        headers["headers"] = new JsonObject { ["Prefer"] = 5 };
        Failure(Batch(headers)).ShouldContain("Prefer");
    }
}

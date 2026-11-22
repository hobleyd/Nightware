using System.Text.Json.Nodes;

using Nightware.Api.Composition;
using Nightware.Application.Mail;
using Nightware.Conformance.Tests.Harness;

namespace Nightware.Conformance.Tests;

/// <summary>Tests of the harness itself, so a broken harness cannot make every fixture pass or skip.</summary>
public sealed class HarnessTests
{
    [Fact]
    public void Every_fixture_parses_and_is_named_after_its_operation()
    {
        var files = ConformanceRunner.FixtureFiles();

        files.ShouldNotBeEmpty();
        foreach (var file in files)
        {
            var fixture = ConformanceRunner.Load(file);
            Path.GetFileNameWithoutExtension(file).ShouldBe(fixture.OperationId, file);
            fixture.Request.Path.ShouldStartWith("/", Case.Sensitive, file);
        }
    }

    [Fact]
    public void Arrangements_are_discovered()
    {
        ArrangementRegistry.ByOperationId.ShouldContainKey("example.mail-folders.list");
        ArrangementRegistry.Find("no.such.operation").ShouldBeNull();
    }

    [Fact]
    public void Requests_are_built_with_encoded_options_headers_and_bodies()
    {
        var fixture = new FixtureRequest(
            "PATCH",
            "/v1.0/users/a%40b.c/messages/m1",
            new Dictionary<string, string> { ["$select"] = "id,isRead", ["$filter"] = "x eq 'y'" },
            new Dictionary<string, string> { ["Prefer"] = "outlook.timezone=\"UTC\"", ["Content-Type"] = "application/json", ["Authorization"] = "Bearer t" },
            JsonNode.Parse("""{"isRead":true}"""));

        using var request = ConformanceRunner.BuildRequest(fixture);

        request.Method.ShouldBe(HttpMethod.Patch);
        request.RequestUri!.OriginalString.ShouldBe("/v1.0/users/a%40b.c/messages/m1?$select=id%2CisRead&$filter=x%20eq%20%27y%27");
        request.Headers.GetValues("Prefer").Single().ShouldBe("outlook.timezone=\"UTC\"");
        request.Content!.Headers.ContentType!.MediaType.ShouldBe("application/json");
    }

    [Fact]
    public void Content_headers_travel_with_the_body()
    {
        var fixture = new FixtureRequest("PUT", "/v1.0/x", new Dictionary<string, string>(),
            new Dictionary<string, string> { ["Content-Type"] = "application/octet-stream", ["Content-Range"] = "bytes 0-3/10" }, JsonValue.Create("abcd"));

        using var request = ConformanceRunner.BuildRequest(fixture);

        request.Content!.Headers.ContentRange!.ToString().ShouldBe("bytes 0-3/10");
    }

    [Fact]
    public async Task A_base64_body_is_sent_as_its_bytes_with_the_fixtures_type()
    {
        var fixture = new FixtureRequest("PUT", "/v1.0/me/photo/$value", new Dictionary<string, string>(),
            new Dictionary<string, string> { ["Content-Type"] = "image/jpeg" }, null) { BodyBase64 = Convert.ToBase64String([1, 2, 255]) };

        using var request = ConformanceRunner.BuildRequest(fixture);

        request.Content!.Headers.ContentType!.MediaType.ShouldBe("image/jpeg");
        (await request.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken)).ShouldBe([1, 2, 255]);
    }

    [Fact]
    public void A_captured_token_placeholder_is_replaced_by_the_signed_token()
    {
        var fixture = new FixtureRequest("GET", "/v1.0/me/mailFolders/delta", new Dictionary<string, string> { ["$deltatoken"] = ConformanceRunner.TokenPlaceholder },
            new Dictionary<string, string>(), null);

        using var request = ConformanceRunner.BuildRequest(fixture, "abc.def+/");

        request.RequestUri!.OriginalString.ShouldBe("/v1.0/me/mailFolders/delta?$deltatoken=abc.def%2B%2F");
    }

    [Fact]
    public void Normaliser_drops_opaque_annotations_and_masks_links_and_correlation()
    {
        var node = JsonNode.Parse("""
            {"@odata.context":"x","@odata.nextLink":"https://h/v1.0/a?$skiptoken=1",
             "value":[{"@odata.etag":"e","id":"1","nested":{"@odata.context":"y"}}],
             "error":{"innerError":{"date":"d","request-id":"r","client-request-id":"c","other":"kept"}}}
            """);

        var links = ResponseNormaliser.Normalise(node);

        links.ShouldBe(["https://h/v1.0/a?$skiptoken=1"]);
        JsonNode.DeepEquals(node, JsonNode.Parse("""
            {"@odata.nextLink":"<link>","value":[{"id":"1","nested":{}}],
             "error":{"innerError":{"date":"<volatile>","request-id":"<volatile>","client-request-id":"<volatile>","other":"kept"}}}
            """)).ShouldBeTrue(node!.ToJsonString());
    }

    [Fact]
    public void Ignore_paths_support_wildcards_and_dotted_keys()
    {
        var node = JsonNode.Parse("""{"value":[{"@odata.type":"t","id":"1","a":{"b":1,"c":2}},{"@odata.type":"t","id":"2"}],"x":{"p":1,"q":2}}""");

        ResponseNormaliser.Normalise(node, ["value/*/@odata.type", "value/*/a/b", "x/*", "missing/path"]);

        JsonNode.DeepEquals(node, JsonNode.Parse("""{"value":[{"id":"1","a":{"c":2}},{"id":"2"}],"x":{}}""")).ShouldBeTrue(node!.ToJsonString());
    }

    [Fact]
    public void A_differing_body_fails_the_comparison()
    {
        var fixture = ConformanceRunner.Load(Path.Combine(ConformanceRunner.ExamplesFolder, "example.mail-folders.list.json"));
        var arrangement = new ConformanceArrangement(fixture.OperationId, static _ => { });

        Should.Throw<ShouldAssertException>(() => ConformanceRunner.AssertJson(fixture, arrangement, """{"value":[]}"""));
    }

    [Fact]
    public void Links_off_the_configured_base_fail_the_comparison()
    {
        var fixture = ConformanceRunner.Load(Path.Combine(ConformanceRunner.ExamplesFolder, "example.mail-folders.list.json"));
        var expected = fixture.Response.Body!.DeepClone().AsObject();
        expected["@odata.nextLink"] = "https://elsewhere.example/v1.0/x";
        var arrangement = new ConformanceArrangement(fixture.OperationId, static _ => { }) { IgnorePaths = ["value/*/wellKnownName"] };

        Should.Throw<ShouldAssertException>(() => ConformanceRunner.AssertJson(fixture, arrangement, expected.ToJsonString()))
            .Message.ShouldContain("not on the configured Graph base");
    }

    [Fact]
    public void Port_fakes_are_created_once_and_can_be_replaced()
    {
        var fakes = new PortFakes();

        fakes.For<IMailFolderReader>().ShouldBeSameAs(fakes.For<IMailFolderReader>());
        var real = Substitute.For<IMailSender>();
        fakes.Use(real);
        fakes.For<IMailSender>().ShouldBeSameAs(real);
    }

    [Fact]
    public async Task A_stub_port_answers_501_through_the_real_program()
    {
        // IMailFolderReader is implemented by the JMAP adapter now (Phase 2), so the stub is put in
        // explicitly; what this pins is that a NotImplementedPort surfaces as Graph's 501 envelope.
        var fixture = new GraphFixture(
            "probe",
            null,
            new FixtureRequest("GET", "/v1.0/me/conformanceExample/mailFolders", new Dictionary<string, string>(), new Dictionary<string, string> { ["Authorization"] = "Bearer x" }, null),
            new FixtureResponse(501, new Dictionary<string, string>(), JsonNode.Parse("""{"error":{"code":"notImplemented","message":"IMailFolderReader.ListAsync is not implemented yet.","innerError":{"date":"","request-id":"","client-request-id":""}}}"""), null),
            "inline");
        var example = ArrangementRegistry.Find("example.mail-folders.list")!;

        await ConformanceRunner.AssertConformsAsync(
            fixture,
            example with { Arrange = static fakes => fakes.Use(NotImplementedPort<IMailFolderReader>.Create()) },
            TestContext.Current.CancellationToken);
    }
}

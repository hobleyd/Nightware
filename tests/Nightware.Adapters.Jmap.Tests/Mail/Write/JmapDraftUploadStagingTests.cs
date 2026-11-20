using System.Text.Json.Nodes;
using Nightware.Adapters.Jmap.Mail.Write;
using Nightware.Adapters.Jmap.Tests.Mail.Write.Support;
using Nightware.Adapters.Jmap.Tests.Support;
using Nightware.Domain.Kernel;
using Nightware.Domain.Mail;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Nightware.Adapters.Jmap.Tests.Mail.Write;

public sealed class JmapDraftUploadStagingTests : IAsyncLifetime
{
    private static readonly MailboxRef Alice = new("alice@nightware.localhost");
    private static readonly MessageId Draft = new("d.ZHJhZnRAZXhhbXBsZS5jb20");
    private readonly FakeJmapServer _jmap = new(Session());

    /// <summary>Metadata, and Blob/upload with Stalwart's 7.5 MB limit.</summary>
    private static string Session()
    {
        var document = JsonNode.Parse(JmapTestHost.SessionWithMetadata())!.AsObject();
        document["capabilities"]!["urn:ietf:params:jmap:blob"] = new JsonObject();
        document["accounts"]!["c"]!["accountCapabilities"]!["urn:ietf:params:jmap:blob"] = new JsonObject { ["maxSizeBlobSet"] = 7499488 };
        return document.ToJsonString();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _jmap.Locate(Draft.Value, "qaaaaaf");
        _jmap.On("Email/set", """{"accountId":"c","oldState":"1","newState":"2","updated":{"qaaaaaf":null}}""");
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _jmap.DisposeAsync();

    private JmapDraftUploadStaging Staging() => new(_jmap.Client(), _jmap.Locator, _jmap.Blobs());

    private void StoredDraft(string keywords = """{"$draft":true}""", string metadata = "{}") =>
        _jmap.On("Email/get", $$$"""{"accountId":"c","state":"1","list":[{"id":"qaaaaaf","keywords":{{{keywords}}},"metadata":{{{metadata}}}}],"notFound":[]}""");

    private static string Stored(long received, string blobs) =>
        """{"nightware.upload":{"sessions":{"s1":{"name":"big.bin","type":"application/octet-stream","size":10,"inline":false,"blobs":"""
        + blobs + ""","received":""" + received.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}}}}";

    private JsonNode Written() => _jmap.Single("Email/set")["update"]!["qaaaaaf"]!["metadata/nightware.upload"]!;

    [Fact]
    public async Task Begin_records_the_session_in_the_draft_s_metadata()
    {
        StoredDraft();

        var state = (await Staging().BeginAsync(Alice, Draft, new DraftUploadFile("big.bin", "application/octet-stream", 10) { ContentId = "c1" }, Ct)).Value;

        state.Received.ShouldBe(0);
        var session = Written()["sessions"]![state.SessionId]!;
        session["name"]!.GetValue<string>().ShouldBe("big.bin");
        session["size"]!.GetValue<long>().ShouldBe(10);
        session["cid"]!.GetValue<string>().ShouldBe("c1");
        session["blobs"]!.AsArray().ShouldBeEmpty();
    }

    [Fact]
    public async Task Only_a_draft_takes_a_session()
    {
        StoredDraft(keywords: """{"$seen":true}""");

        (await Staging().BeginAsync(Alice, Draft, new DraftUploadFile("a", "b", 1), Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public async Task A_chunk_is_appended_while_the_list_is_short()
    {
        StoredDraft(metadata: Stored(4, """[{"id":"B1","size":4}]"""));
        _jmap.StubUpload("B2", "application/octet-stream", 6);

        (await Staging().AppendAsync(Alice, Draft, "s1", 4, 6, 10, new MemoryStream(new byte[6]), Ct)).Value.IsComplete.ShouldBeTrue();

        _jmap.Calls().ShouldNotContain(c => c.Method == "Blob/upload");
        Written()["sessions"]!["s1"]!["blobs"]!.ToJsonString().ShouldBe("""[{"id":"B1","size":4},{"id":"B2","size":6}]""");
    }

    [Fact]
    public async Task A_chunk_is_merged_into_the_last_blob_within_the_server_s_limit_once_the_list_is_long()
    {
        var earlier = string.Join(",", Enumerable.Range(0, JmapDraftUploadStaging.MergeAfter - 1).Select(static i => $$$"""{"id":"P{{{i}}}","size":0}"""));
        StoredDraft(metadata: Stored(4, "[" + earlier + """,{"id":"B1","size":4}]"""));
        _jmap.StubUpload("B2", "application/octet-stream", 6);
        _jmap.On("Blob/upload", """{"accountId":"c","created":{"merged":{"id":"B12","size":10}}}""");

        var state = (await Staging().AppendAsync(Alice, Draft, "s1", 4, 6, 10, new MemoryStream(new byte[6]), Ct)).Value;

        state.IsComplete.ShouldBeTrue();
        _jmap.Single("Blob/upload")["create"]!["merged"]!["data"]!.ToJsonString().ShouldBe("""[{"blobId":"B1"},{"blobId":"B2"}]""");
        Written()["sessions"]!["s1"]!["blobs"]!.AsArray()[^1]!.ToJsonString().ShouldBe("""{"id":"B12","size":10}""");
        Written()["sessions"]!["s1"]!["received"]!.GetValue<long>().ShouldBe(10);
    }

    [Theory]
    [InlineData(3L, 10L)]
    [InlineData(4L, 12L)]
    public async Task A_chunk_out_of_order_or_for_another_size_stores_nothing(long first, long total)
    {
        StoredDraft(metadata: Stored(4, """[{"id":"B1","size":4}]"""));

        (await Staging().AppendAsync(Alice, Draft, "s1", first, 2, total, new MemoryStream(new byte[2]), Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        _jmap.Calls().ShouldNotContain(c => c.Method == "Email/set" || c.Method == "Blob/upload");
    }

    [Fact]
    public async Task An_unknown_session_is_404()
    {
        StoredDraft();

        (await Staging().AppendAsync(Alice, Draft, "nope", 0, 1, 10, new MemoryStream(new byte[1]), Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Read_joins_the_blobs_in_order()
    {
        StoredDraft(metadata: Stored(10, """[{"id":"B1","size":4},{"id":"B2","size":6}]"""));
        foreach (var (blob, text) in new[] { ("B1", "abcd"), ("B2", "efghij") })
        {
            _jmap.Host.Server.Given(Request.Create().WithPath($"/jmap/download/c/{blob}/chunk").UsingGet())
                .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/octet-stream").WithBody(text));
        }

        var content = (await Staging().ReadAsync(Alice, Draft, "s1", Ct)).Value;

        System.Text.Encoding.ASCII.GetString(content.Span).ShouldBe("abcdefghij");
    }

    [Fact]
    public async Task A_server_without_metadata_cannot_hold_a_session()
    {
        await using var plain = new FakeJmapServer();
        plain.Locate(Draft.Value, "qaaaaaf");

        (await new JmapDraftUploadStaging(plain.Client(), plain.Locator, plain.Blobs()).BeginAsync(Alice, Draft, new DraftUploadFile("a", "b", 1), Ct))
            .Error!.Kind.ShouldBe(ErrorKind.Unsupported);
    }
}

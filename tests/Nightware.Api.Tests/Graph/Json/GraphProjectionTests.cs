using System.Text.Json.Nodes;

using Nightware.Api.Graph.Json;
using Nightware.Application.Common.Query;

namespace Nightware.Api.Tests.Graph.Json;

public sealed class GraphProjectionTests
{
    private static JsonObject Message() => JsonNode.Parse("""
        {
          "@odata.type": "#microsoft.graph.eventMessageRequest",
          "@odata.etag": "W/\"1\"",
          "id": "m1",
          "subject": "Hi",
          "isRead": false,
          "body": { "contentType": "html", "content": "x" },
          "attachments": [
            { "@odata.type": "#microsoft.graph.fileAttachment", "id": "a1", "name": "x.pdf", "contentType": "application/pdf", "size": 3, "isInline": false, "contentId": null }
          ]
        }
        """)!.AsObject();

    [Fact]
    public void No_select_leaves_the_object_untouched()
    {
        var item = Message();

        GraphProjection.Apply(item, null).ShouldBeSameAs(item);
        JsonNode.DeepEquals(item, Message()).ShouldBeTrue();
    }

    [Fact]
    public void Select_keeps_named_properties_id_and_etag_and_drops_odata_type()
    {
        var item = GraphProjection.Apply(Message(), new HashSet<string>(StringComparer.Ordinal) { "Subject" });

        item.Select(p => p.Key).ShouldBe(["@odata.etag", "id", "subject"]);
    }

    [Fact]
    public void Removed_marker_survives_a_select()
    {
        var item = JsonNode.Parse("""{"id":"x","@removed":{"reason":"deleted"},"subject":"s"}""")!.AsObject();

        GraphProjection.Apply(item, new HashSet<string> { "isRead" }).Select(p => p.Key).ShouldBe(["id", "@removed"]);
    }

    [Fact]
    public void Nested_expand_select_projects_each_element_and_keeps_its_odata_type()
    {
        var expand = new[] { new ExpandClause("attachments", new HashSet<string> { "id", "name", "contentType", "size", "isInline" }) };

        var item = GraphProjection.Apply(Message(), null, expand);

        item["@odata.type"].ShouldNotBeNull();
        item["attachments"]![0]!.AsObject().Select(p => p.Key)
            .ShouldBe(["@odata.type", "id", "name", "contentType", "size", "isInline"]);
    }

    [Fact]
    public void Expanded_navigation_survives_the_top_level_select()
    {
        var expand = new[] { new ExpandClause("attachments", new HashSet<string> { "name" }) };

        var item = GraphProjection.Apply(Message(), new HashSet<string> { "subject" }, expand);

        item.Select(p => p.Key).ShouldBe(["@odata.etag", "id", "subject", "attachments"]);
        item["attachments"]![0]!.AsObject().Select(p => p.Key).ShouldBe(["@odata.type", "id", "name"]);
    }

    [Fact]
    public void Expand_without_select_or_target_is_harmless()
    {
        var item = GraphProjection.Apply(Message(), null, [new ExpandClause("attachments"), new ExpandClause("missing", new HashSet<string> { "x" })]);

        JsonNode.DeepEquals(item, Message()).ShouldBeTrue();
    }

    [Fact]
    public void Expand_select_applies_to_a_single_navigation_object()
    {
        var item = JsonNode.Parse("""{"id":"e","event":{"id":"ev","subject":"s","isOrganizer":true}}""")!.AsObject();

        GraphProjection.Apply(item, null, [new ExpandClause("event", new HashSet<string> { "isOrganizer" })]);

        item["event"]!.AsObject().Select(p => p.Key).ShouldBe(["id", "isOrganizer"]);
    }
}

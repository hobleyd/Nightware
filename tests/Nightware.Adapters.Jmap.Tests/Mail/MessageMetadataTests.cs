using System.Text.Json.Nodes;
using Nightware.Adapters.Jmap.Mail;
using Nightware.Domain.Calendar;
using Nightware.Domain.Mail;

namespace Nightware.Adapters.Jmap.Tests.Mail;

public sealed class MessageMetadataTests
{
    private static readonly DateTimeTimeZone Due = new(new DateTime(2026, 11, 25, 17, 0, 0), "AUS Eastern Standard Time");

    [Fact]
    public void Round_trips_flag_dates_categories_and_importance()
    {
        var metadata = new MessageMetadata
        {
            Flag = new FollowUpFlag(FlagStatus.Flagged) { DueDateTime = Due },
            Categories = ["Blue category", "Ünïcode"],
            Importance = Importance.Low,
        };

        var read = MessageMetadata.Read(new JsonObject { [MessageMetadata.Namespace] = metadata.ToJson() });

        read.Flag.ShouldBe(metadata.Flag);
        read.Categories.ShouldBe(["Blue category", "Ünïcode"]);
        read.Importance.ShouldBe(Importance.Low);
    }

    [Fact]
    public void Nothing_set_is_no_namespace()
    {
        MessageMetadata.Empty.ToJson().ShouldBeNull();
        MessageMetadata.Read(null).ToJson().ShouldBeNull();
        MessageMetadata.Read(new JsonObject { ["other.vendor"] = new JsonObject() }).ToJson().ShouldBeNull();
    }

    [Fact]
    public void Malformed_entries_read_as_unset()
    {
        var read = MessageMetadata.Read(JsonNode.Parse("""{"nightware.ms":{"flag":{"status":"maybe"},"categories":"Red","importance":"urgent"}}""")!.AsObject());

        (read.Flag, read.Categories, read.Importance).ShouldBe((null, null, null));
    }

    [Fact]
    public void Apply_sets_what_the_patch_names_and_clears_on_default_values()
    {
        var stored = new MessageMetadata { Flag = new FollowUpFlag(FlagStatus.Complete), Categories = ["Red"], Importance = Importance.High };

        var recategorised = stored.Apply(new MessagePatch { Categories = ["Green"] });
        recategorised.Categories.ShouldBe(["Green"]);
        (recategorised.Flag, recategorised.Importance).ShouldBe((stored.Flag, stored.Importance));
        stored.Apply(new MessagePatch { Flag = FollowUpFlag.NotFlagged, Categories = [], Importance = Importance.Normal })
            .ToJson().ShouldBeNull();
        stored.Apply(new MessagePatch { IsRead = true }).ShouldBe(stored);
    }
}

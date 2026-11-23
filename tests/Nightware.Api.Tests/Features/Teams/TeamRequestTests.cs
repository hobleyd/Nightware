using System.Text.Json.Nodes;

using Nightware.Api.Features.Teams;
using Nightware.Api.Features.Teams.Dto;
using Nightware.Application.Teams;
using Nightware.Domain.Chats;
using Nightware.Domain.Teams;

namespace Nightware.Api.Tests.Features.Teams;

public sealed class TeamRequestTests
{
    private const string TeamId = "6LOrxifQ5p4vRt2I3iplImWqdpjlADT";

    private static JsonObject Json(string text) => JsonNode.Parse(text)!.AsObject();

    [Theory]
    [InlineData("https://graph.microsoft.com/v1.0/teamsTemplates('standard')")]
    [InlineData("https://graph.microsoft.com/beta/teamsTemplates/standard")]
    [InlineData("teamsTemplates('Standard')")]
    public void The_standard_template_is_named_either_way(string bind)
    {
        var creation = TeamRequests.Creation(new JsonObject { [TeamRequests.TemplateBind] = bind, ["displayName"] = "Probe", ["description"] = "About" });

        creation.Value.ShouldBe(new TeamCreation("Probe", "About", null, []) { Members = creation.Value.Members });
        creation.Value.Members.ShouldBeEmpty();
    }

    [Fact]
    public void A_creation_needs_a_body() => TeamRequests.Creation(null).Error!.Kind.ShouldBe(Nightware.Domain.Kernel.ErrorKind.Validation);

    [Fact]
    public void An_update_reads_name_description_and_visibility()
    {
        TeamRequests.Update(Json("""{"displayName":"N","description":"D","visibility":"PUBLIC"}""")).Value.ShouldBe(new TeamUpdate("N", "D", TeamVisibility.Public));
        TeamRequests.Update(Json("""{"memberSettings":{}}""")).Value.IsEmpty.ShouldBeTrue();
        TeamRequests.Update(null).IsSuccess.ShouldBeFalse();
    }

    [Theory]
    [InlineData("""{"roles":["owner"]}""", true)]
    [InlineData("""{"roles":["Owner"]}""", true)]
    [InlineData("""{"roles":[]}""", false)]
    public void Roles_say_owner_or_not(string body, bool isOwner) =>
        TeamRequests.IsOwner(Json(body)).Value.ShouldBe(isOwner);

    [Fact]
    public void Channel_bodies_are_read_and_only_standard_ones_made()
    {
        TeamRequests.ChannelCreation(Json("""{"displayName":"Design","description":"UI","membershipType":"Standard"}""")).Value.ShouldBe(new ChannelCreation("Design", "UI"));
        TeamRequests.ChannelCreation(Json("""{"displayName":"Design","membershipType":"shared"}""")).Error!.Target.ShouldBe("membershipType");
        TeamRequests.ChannelCreation(null).IsSuccess.ShouldBeFalse();
        TeamRequests.ChannelUpdate(Json("""{"description":"x"}""")).Value.ShouldBe(new ChannelUpdate(null, "x"));
        TeamRequests.ChannelUpdate(null).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void Paths_and_contexts_use_graphs_key_form()
    {
        TeamPaths.Team(TeamId).ShouldBe($"/teams('{TeamId}')");
        TeamPaths.Operation(TeamId, "create." + TeamId).ShouldBe($"/teams('{TeamId}')/operations('create.{TeamId}')");
        TeamPaths.Message(TeamId, "xtugjbpy", "209").ShouldBe($"teams('{TeamId}')/channels('xtugjbpy')/messages('209')");
    }

    [Fact]
    public void A_post_carries_replies_only_when_they_were_read()
    {
        var post = new ChatMessage("209", ChatMessageType.Message, DateTimeOffset.UnixEpoch);

        TeamDtosOf(new ChannelPostView(post)).ContainsKey("replies").ShouldBeFalse();
        TeamDtosOf(new ChannelPostView(post, []))["replies"]!.AsArray().ShouldBeEmpty();
    }

    [Fact]
    public void A_channel_is_standard_with_posts_and_no_address()
    {
        var dto = ChannelDto.From(new Channel("xtugjbpy", "General"));

        dto.MembershipType.ShouldBe("standard");
        dto.LayoutType.ShouldBe("post");
        dto.Email.ShouldBeEmpty();
        TeamDto.From(new Team(TeamId, "T")).Visibility.ShouldBe("private");
    }

    private static JsonObject TeamDtosOf(ChannelPostView view) => Nightware.Api.Graph.Json.GraphJson.ToObject(ChannelPostDto.From(view));
}

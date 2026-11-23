using System.Text.Json.Nodes;

using Nightware.Api.Features.Chats;
using Nightware.Api.Features.Chats.Dto;
using Nightware.Api.Graph.Json;
using Nightware.Application.Chats;
using Nightware.Application.Common.Query;
using Nightware.Domain.Chats;

using SortDirection = Nightware.Application.Common.Query.SortDirection;

namespace Nightware.Api.Tests.Features.Chats;

/// <summary>The chat request readers and DTO mapping on their own.</summary>
public sealed class ChatRequestTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("https://graph.microsoft.com/v1.0/users('8b081ef6-4792-4def-b2c9-c363a1bf41d5')", "8b081ef6-4792-4def-b2c9-c363a1bf41d5")]
    [InlineData("https://graph.microsoft.com/beta/users/bob%40example.com", "bob@example.com")]
    [InlineData("users('bob@example.com')", "bob@example.com")]
    [InlineData("https://graph.microsoft.com/v1.0/users/bob-id/", "bob-id")]
    [InlineData("https://graph.microsoft.com/v1.0/groups('g')", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void A_user_bind_names_its_user(string? bind, string? user) => ChatRequests.UserOfBind(bind).ShouldBe(user);

    [Theory]
    [InlineData("https://graph.microsoft.com/v1.0/chats/abcd1234/messages/59", "59")]
    [InlineData("https://graph.microsoft.com/v1.0/chats('abcd1234')/messages('59')", "59")]
    public void A_message_bind_names_a_message_of_this_chat(string bind, string id) =>
        ChatRequests.MessageOfBind(new JsonObject { [ChatRequests.MessageBind] = bind }, "abcd1234").Value.ShouldBe(id);

    [Theory]
    [InlineData("https://graph.microsoft.com/v1.0/chats/other999/messages/59")]
    [InlineData("https://graph.microsoft.com/v1.0/chats/abcd1234")]
    [InlineData(null)]
    public void Any_other_message_bind_is_400(string? bind) =>
        ChatRequests.MessageOfBind(bind is null ? [] : new JsonObject { [ChatRequests.MessageBind] = bind }, "abcd1234").Error!.Target.ShouldBe(ChatRequests.MessageBind);

    [Fact]
    public void A_page_query_takes_top_skiptoken_and_the_listed_expands()
    {
        var query = ChatRequests.PageQuery(new ItemQuery { Top = 5, SkipToken = "10", Expand = [new ExpandClause("lastMessagePreview")] }, "members", "lastMessagePreview").Value;

        query.Top.ShouldBe(5);
        query.SkipToken.ShouldBe("10");
        query.WithLastMessage.ShouldBeTrue();
        ChatRequests.PageQuery(ItemQuery.Default).Value.Top.ShouldBe(ChatLimits.DefaultPageSize);
        ChatRequests.PageQuery(new ItemQuery { OrderBy = [new OrderBy("createdDateTime", SortDirection.Descending)] }).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void A_page_query_refuses_what_it_cannot_do()
    {
        ChatRequests.PageQuery(new ItemQuery { Skip = 2 }).IsSuccess.ShouldBeFalse();
        ChatRequests.PageQuery(new ItemQuery { Count = true }).IsSuccess.ShouldBeFalse();
        ChatRequests.PageQuery(new ItemQuery { OrderBy = [new OrderBy("createdDateTime", SortDirection.Ascending)] }).Error!.Target.ShouldBe("$orderby");
        ChatRequests.PageQuery(new ItemQuery { Expand = [new ExpandClause("members")] }).Error!.Target.ShouldBe("$expand");
        ChatRequests.PageQuery(new ItemQuery { Top = 60 }).Error!.Target.ShouldBe("$top");
    }

    [Fact]
    public void A_draft_reads_body_mentions_and_subject()
    {
        var body = JsonNode.Parse("""
            {"subject":"s","importance":"high","body":{"contentType":"HTML","content":"<p>x</p>"},
             "mentions":[{"id":0,"mentionText":"Bob","mentioned":{"user":{"id":"bob-id"}}},
                         {"id":1,"mentionText":"All","mentioned":{"conversation":{"id":"abcd1234"}}}]}
            """)!.AsObject();

        var draft = ChatRequests.Draft(body).Value;

        draft.Body.ShouldBe(new ChatMessageBody(ChatBodyType.Html, "<p>x</p>"));
        draft.Subject.ShouldBe("s");
        draft.Mentions.ShouldBe([new ChatMentionDraft(0, "Bob", "bob-id"), new ChatMentionDraft(1, "All", null, true)]);
        ChatRequests.Draft(JsonNode.Parse("""{"body":{"content":"plain"}}""")!.AsObject()).Value.Body.ContentType.ShouldBe(ChatBodyType.Text);
        ChatRequests.Draft(null).IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void The_user_of_a_for_user_body_is_read()
    {
        ChatRequests.UserId(JsonNode.Parse("""{"user":{"id":"u1","tenantId":"t"}}""")!.AsObject()).ShouldBe("u1");
        ChatRequests.UserId([]).ShouldBeNull();
        ChatRequests.UserId(null).ShouldBeNull();
    }

    [Fact]
    public void A_meeting_chat_carries_its_join_link_and_a_preview_only_when_asked()
    {
        var chat = new Chat("mtg12345", ChatType.Meeting)
        {
            Topic = "Review",
            JoinWebUrl = "https://cloud.example.com/call/mtg12345",
            LastMessage = new ChatMessage("5", ChatMessageType.Message, At) { DeletedDateTime = At },
        };

        var dto = GraphJson.ToObject(ChatDto.From(chat, withLastMessage: true));

        dto["chatType"]!.GetValue<string>().ShouldBe("meeting");
        dto["onlineMeetingInfo"]!["joinWebUrl"]!.GetValue<string>().ShouldBe("https://cloud.example.com/call/mtg12345");
        dto["lastMessagePreview"]!["isDeleted"]!.GetValue<bool>().ShouldBeTrue();
        dto.ContainsKey("members").ShouldBeFalse();
        GraphJson.ToObject(ChatDto.From(chat)).ContainsKey("lastMessagePreview").ShouldBeFalse();
        GraphJson.ToObject(ChatDto.From(new Chat("a1b2c3d4", ChatType.Group)))["onlineMeetingInfo"].ShouldBeNull();
    }

    [Fact]
    public void A_message_maps_attachments_reactions_and_channel_identity()
    {
        var message = new ChatMessage("76", ChatMessageType.Message, At)
        {
            ChannelIdentity = new ChannelIdentity("team1", "chan1234"),
            ReplyToId = "70",
            Subject = "Post",
            LastEditedDateTime = At.AddMinutes(1),
            LastModifiedDateTime = At.AddMinutes(1),
            From = new ChatIdentity(null, "Guest", ChatIdentityKind.Guest),
            Body = new ChatMessageBody(ChatBodyType.Text, "hi"),
            Attachments = [new ChatMessageAttachment("467", ChatMessageAttachment.Reference, "https://cloud.example.com/f/467", "a.txt")],
            Reactions = [new ChatReaction("👍", At, new ChatIdentity("bob-id", "Bob"))],
            Mentions = [new ChatMention(0, "Team", new ChatMentioned.Conversation("chan1234", "General", "channel"))],
        };

        var dto = GraphJson.ToObject(ChatMessageDto.Of(message));

        dto["channelIdentity"]!["teamId"]!.GetValue<string>().ShouldBe("team1");
        dto["chatId"].ShouldBeNull();
        dto["replyToId"]!.GetValue<string>().ShouldBe("70");
        dto["body"]!["contentType"]!.GetValue<string>().ShouldBe("text");
        dto["from"]!["user"]!["userIdentityType"]!.GetValue<string>().ShouldBe("anonymousGuest");
        dto["etag"]!.GetValue<string>().ShouldBe(At.AddMinutes(1).ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        dto["attachments"]![0]!["contentType"]!.GetValue<string>().ShouldBe("reference");
        dto["attachments"]![0]!["contentUrl"]!.GetValue<string>().ShouldBe("https://cloud.example.com/f/467");
        dto["reactions"]![0]!["user"]!["user"]!["id"]!.GetValue<string>().ShouldBe("bob-id");
        dto["mentions"]![0]!["mentioned"]!["conversation"]!["conversationIdentityType"]!.GetValue<string>().ShouldBe("channel");
        dto["mentions"]![0]!["mentioned"]!["user"].ShouldBeNull();
    }

    [Fact]
    public void An_application_sender_is_an_application_identity()
    {
        var dto = GraphJson.ToObject(ChatMessageDto.Of(new ChatMessage("1", ChatMessageType.Message, At) { From = new ChatIdentity(null, "System", ChatIdentityKind.Application) }));

        dto["from"]!["application"]!["applicationIdentityType"]!.GetValue<string>().ShouldBe("bot");
        dto["from"]!["user"].ShouldBeNull();
        dto["lastModifiedDateTime"]!.GetValue<string>().ShouldBe("2026-11-23T09:00:00Z");
    }

    [Fact]
    public void Event_details_carry_their_graph_types()
    {
        var alice = new ChatIdentity("alice-id", "Alice");
        var bob = new ChatIdentity("bob-id", "Bob");

        var added = GraphJson.ToObject(EventDetailDtos.From(new ChatEventDetail.MembersAdded(alice, [bob]))!);
        added["@odata.type"]!.GetValue<string>().ShouldBe("#microsoft.graph.membersAddedEventMessageDetail");
        added["visibleHistoryStartDateTime"]!.GetValue<string>().ShouldBe("0001-01-01T00:00:00Z");
        added["members"]![0]!["id"]!.GetValue<string>().ShouldBe("bob-id");
        added["initiator"]!["user"]!["id"]!.GetValue<string>().ShouldBe("alice-id");

        GraphJson.ToObject(EventDetailDtos.From(new ChatEventDetail.MembersDeleted(alice, [bob]))!)["@odata.type"]!.GetValue<string>()
            .ShouldBe("#microsoft.graph.membersDeletedEventMessageDetail");

        var ended = GraphJson.ToObject(EventDetailDtos.From(new ChatEventDetail.CallEnded(null, TimeSpan.FromSeconds(14), [alice]))!);
        ended["@odata.type"]!.GetValue<string>().ShouldBe("#microsoft.graph.callEndedEventMessageDetail");
        ended["callDuration"]!.GetValue<string>().ShouldBe("PT14S");
        ended["callEventType"]!.GetValue<string>().ShouldBe("call");
        ended["callParticipants"]![0]!["participant"]!["user"]!["id"]!.GetValue<string>().ShouldBe("alice-id");
        ended["initiator"].ShouldBeNull();
        GraphJson.ToObject(EventDetailDtos.From(new ChatEventDetail.CallEnded(alice, null, []))!)["callDuration"].ShouldBeNull();
        EventDetailDtos.From(null).ShouldBeNull();
    }
}

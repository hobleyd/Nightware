using Nightware.Domain.Chats;

namespace Nightware.Domain.Tests.Chats;

public sealed class ChatTests
{
    private static readonly DateTimeOffset Sent = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_messages_etag_changes_with_its_last_change()
    {
        var message = new ChatMessage("59", ChatMessageType.Message, Sent);

        message.ETag.ShouldBe(Sent.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        (message with { LastModifiedDateTime = Sent.AddMinutes(1) }).ETag.ShouldNotBe(message.ETag);
    }

    [Fact]
    public void A_message_defaults_to_an_empty_html_body_and_normal_importance()
    {
        var message = new ChatMessage("1", ChatMessageType.Message, Sent);

        message.Body.ShouldBe(new ChatMessageBody(ChatBodyType.Html, string.Empty));
        message.Importance.ShouldBe("normal");
        message.Attachments.ShouldBeEmpty();
        message.Mentions.ShouldBeEmpty();
        message.Reactions.ShouldBeEmpty();
        ChatMessageBody.SystemEvent.Content.ShouldBe("<systemEventMessage/>");
        ChatMessageBody.Deleted.Content.ShouldBeEmpty();
    }

    [Fact]
    public void A_chat_is_shown_and_unread_until_told_otherwise_and_members_see_all_history()
    {
        new Chat("abcd1234", ChatType.Group).Viewpoint.ShouldBe(new ChatViewpoint(false, null));
        var member = new ChatMember("m.1", "Alice");
        member.Roles.ShouldBeEmpty();
        member.VisibleHistoryStartDateTime.ShouldBe(DateTimeOffset.MinValue);
        ChatMember.OwnerRole.ShouldBe("owner");
        ChatMessageAttachment.Reference.ShouldBe("reference");
    }

    [Fact]
    public void Event_details_carry_their_initiator()
    {
        var alice = new ChatIdentity("id-alice", "Alice");

        new ChatEventDetail.MembersAdded(alice, []).Initiator.ShouldBe(alice);
        new ChatEventDetail.CallEnded(null, TimeSpan.FromSeconds(14), [alice]).Initiator.ShouldBeNull();
        new ChatMentioned.Conversation("abcd1234", "Planning", "chat").ShouldBe(new ChatMentioned.Conversation("abcd1234", "Planning", "chat"));
    }
}

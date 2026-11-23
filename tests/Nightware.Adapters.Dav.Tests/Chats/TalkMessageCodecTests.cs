using System.Text.Json;

using Nightware.Adapters.Dav.Chats.Talk;
using Nightware.Domain.Chats;

namespace Nightware.Adapters.Dav.Tests.Chats;

/// <summary>
/// Talk messages recorded from the dev stack (a group room: a mention, a reply, a reaction, an edit, a delete, a pin, a
/// shared file, a rename, a member removed and added) as Graph messages (ADR 0009 D4).
/// </summary>
public sealed class TalkMessageCodecTests
{
    private static readonly TalkConversation Chat = new("5opkrcg6", "p23 probe renamed") { WebUrl = "https://cloud.example.com/call/5opkrcg6" };
    private static readonly IReadOnlyList<TalkMessage> History = TalkTestServer.RecordedMessages();

    private static ChatIdentity Identify(TalkActor actor) => actor.Id switch
    {
        "alice" when actor.IsUser => new ChatIdentity("id-alice", "Alice Anderson"),
        "bob" when actor.IsUser => new ChatIdentity("id-bob", "Bob Brown"),
        _ => TalkIdentityDefaults.Anonymous(actor),
    };

    private static ChatMessage? Decode(long id, TalkConversation? conversation = null, DateTimeOffset? deletedAt = null) =>
        TalkMessageCodec.ToChatMessage(TalkTestServer.Recorded(id), conversation ?? Chat, Identify, deletedAt);

    [Fact]
    public void Only_comments_and_the_events_teams_has_are_listed()
    {
        History.Where(TalkMessageCodec.IsListed).Select(m => m.Id).ShouldBe([80, 79, 78, 77, 76, 72, 66, 59, 57]);
        History.Where(m => !TalkMessageCodec.IsListed(m)).Select(m => m.SystemMessage)
            .ShouldBe(["message_pinned", "message_pinned", "message_deleted", "message_edited", "reaction", "conversation_created"]);
    }

    [Fact]
    public void An_edited_message_with_a_mention_and_reactions()
    {
        var talk = TalkTestServer.Recorded(59) with
        {
            Reactions = [new TalkReaction("👍", new TalkActor("users", "bob", "Bob Brown"), DateTimeOffset.FromUnixTimeSeconds(1795426351))],
        };

        var message = TalkMessageCodec.ToChatMessage(talk, Chat, Identify)!;

        message.Id.ShouldBe("59");
        message.MessageType.ShouldBe(ChatMessageType.Message);
        message.ChatId.ShouldBe("5opkrcg6");
        message.ChannelIdentity.ShouldBeNull();
        message.ReplyToId.ShouldBeNull();
        message.WebUrl.ShouldBe("https://cloud.example.com/call/5opkrcg6#message_59");
        message.From.ShouldBe(new ChatIdentity("id-alice", "Alice Anderson"));
        message.Body.ShouldBe(new ChatMessageBody(ChatBodyType.Html, "<p>Edited <strong>text</strong> <at id=\"0\">Bob Brown</at></p>"));
        var mention = message.Mentions.ShouldHaveSingleItem();
        mention.ShouldBe(new ChatMention(0, "Bob Brown", new ChatMentioned.User(new ChatIdentity("id-bob", "Bob Brown"))));
        message.CreatedDateTime.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1795426346));
        message.LastEditedDateTime.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1795426359));
        message.LastModifiedDateTime.ShouldBe(message.LastEditedDateTime);
        message.ETag.ShouldBe("1795426359000");
        message.Reactions.ShouldHaveSingleItem().ShouldBe(new ChatReaction("👍", DateTimeOffset.FromUnixTimeSeconds(1795426351), new ChatIdentity("id-bob", "Bob Brown")));
    }

    [Fact]
    public void A_conversation_mention_names_the_chat_and_repeats_reuse_their_id()
    {
        var talk = new TalkMessage(9, "5opkrcg6", new TalkActor("users", "alice", "Alice Anderson"), DateTimeOffset.UnixEpoch)
        {
            Text = "{mention-call1}: {mention-user1}, {mention-user1}!",
            IsMarkdown = true,
            Parameters = new Dictionary<string, TalkParameter>
            {
                ["mention-call1"] = new("call", "5opkrcg6", "p23 probe group"),
                ["mention-user1"] = new("user", "bob", "Bob Brown"),
            },
        };

        var message = TalkMessageCodec.ToChatMessage(talk, Chat, Identify)!;

        message.Body.Content.ShouldBe("<p><at id=\"0\">p23 probe group</at>: <at id=\"1\">Bob Brown</at>, <at id=\"1\">Bob Brown</at>!</p>");
        message.Mentions.Count.ShouldBe(2);
        message.Mentions[0].Mentioned.ShouldBe(new ChatMentioned.Conversation("5opkrcg6", "p23 probe renamed", "chat"));
    }

    [Fact]
    public void A_person_the_directory_does_not_know_keeps_talks_name()
    {
        var talk = new TalkMessage(9, "5opkrcg6", new TalkActor("guests", "abc", "Visitor"), DateTimeOffset.UnixEpoch)
        {
            Text = "hi {mention-federated-user1}",
            Parameters = new Dictionary<string, TalkParameter> { ["mention-federated-user1"] = new("user", "carol", "Carol") },
        };

        var message = TalkMessageCodec.ToChatMessage(talk, Chat, Identify)!;

        message.From.ShouldBe(new ChatIdentity(null, "Visitor", ChatIdentityKind.Guest));
        message.Body.Content.ShouldBe("hi <at id=\"0\">Carol</at>");
        message.Mentions.Single().Mentioned.ShouldBe(new ChatMentioned.User(new ChatIdentity(null, "Carol")));
    }

    [Fact]
    public void A_shared_file_is_a_reference_attachment()
    {
        var message = Decode(76)!;

        message.Body.Content.ShouldBe("<p><attachment id=\"467\"></attachment></p>");
        message.Attachments.ShouldHaveSingleItem().ShouldBe(new ChatMessageAttachment("467", "reference", "https://cloud.example.com/cloud/f/467", "p23-probe.txt"));
    }

    [Fact]
    public void A_deleted_message_has_its_deletion_time_and_an_empty_body()
    {
        var times = TalkMessageCodec.DeletionTimes(History);
        times[72].ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1795426363));

        var message = Decode(72, deletedAt: times[72])!;

        message.DeletedDateTime.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1795426363));
        message.LastModifiedDateTime.ShouldBe(message.DeletedDateTime);
        message.Body.ShouldBe(ChatMessageBody.Deleted);
        message.From!.Id.ShouldBe("id-alice");
        message.Mentions.ShouldBeEmpty();

        // Without Talk's event in the batch, the message's own time stands in.
        Decode(72)!.DeletedDateTime.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1795426360));
    }

    [Fact]
    public void Members_added_and_removed_are_system_events()
    {
        var added = Decode(79)!;
        added.MessageType.ShouldBe(ChatMessageType.SystemEventMessage);
        added.From.ShouldBeNull();
        added.Body.ShouldBe(ChatMessageBody.SystemEvent);
        added.EventDetail.ShouldBe(new ChatEventDetail.MembersAdded(new ChatIdentity("id-alice", "Alice Anderson"), [new ChatIdentity("id-bob", "Bob Brown")]), new EventDetailComparer());

        var removed = (ChatEventDetail.MembersDeleted)Decode(78)!.EventDetail!;
        removed.Members.ShouldHaveSingleItem().Id.ShouldBe("id-bob");
    }

    [Fact]
    public void A_rename_reports_the_new_name()
    {
        Decode(77)!.EventDetail.ShouldBe(new ChatEventDetail.ChatRenamed(new ChatIdentity("id-alice", "Alice Anderson"), "5opkrcg6", "p23 probe renamed"));
    }

    [Fact]
    public void A_rename_in_another_language_has_no_name()
    {
        var talk = TalkTestServer.Recorded(77) with { Text = "Sie haben die Unterhaltung umbenannt" };

        ((ChatEventDetail.ChatRenamed)TalkMessageCodec.ToChatMessage(talk, Chat, Identify)!.EventDetail!).ChatDisplayName.ShouldBeNull();
    }

    [Fact]
    public void A_call_end_reports_its_participants_and_duration_and_talk_as_initiator()
    {
        var call = TalkTestServer.Recorded(61, "chat-history-call.json");

        var detail = (ChatEventDetail.CallEnded)TalkMessageCodec.ToChatMessage(call, Chat, Identify)!.EventDetail!;

        detail.Duration.ShouldBe(new TimeSpan(10, 40, 17));
        detail.Participants.ShouldBe([new ChatIdentity("id-alice", "Alice Anderson")]);
        detail.Initiator.ShouldBe(new ChatIdentity(null, "System", ChatIdentityKind.Application));
    }

    [Theory]
    [InlineData("Call with {user1} ended (Duration 0:14)", 0, 0, 14)]
    [InlineData("Call ended (Duration 2:31:51)", 2, 31, 51)]
    [InlineData("Anruf beendet (Dauer 1:02)", 0, 1, 2)]
    public void Call_durations_are_read_from_the_text(string text, int hours, int minutes, int seconds) =>
        TalkMessageCodec.Duration(text).ShouldBe(new TimeSpan(hours, minutes, seconds));

    [Fact]
    public void A_call_end_without_a_duration_has_none() => TalkMessageCodec.Duration("Call ended").ShouldBeNull();

    [Fact]
    public void Talks_bookkeeping_decodes_to_nothing()
    {
        foreach (var id in new long[] { 75, 74, 73, 71, 67, 56 })
        {
            Decode(id).ShouldBeNull();
        }
    }

    [Fact]
    public void In_a_channel_a_reply_names_its_post_and_a_post_has_its_subject()
    {
        var channel = new TalkConversation("indwxaa7", "General") { Channel = new ChannelIdentity("team1", "indwxaa7") };
        var post = new TalkMessage(100, "indwxaa7", new TalkActor("users", "alice", "Alice"), DateTimeOffset.UnixEpoch) { Text = "Kick-off", ThreadId = 100, IsThread = true, ThreadTitle = "Plans" };
        var reply = new TalkMessage(102, "indwxaa7", new TalkActor("users", "bob", "Bob"), DateTimeOffset.UnixEpoch) { Text = "ok", ThreadId = 100 };

        var decodedPost = TalkMessageCodec.ToChatMessage(post, channel, Identify)!;
        decodedPost.ChatId.ShouldBeNull();
        decodedPost.ChannelIdentity.ShouldBe(new ChannelIdentity("team1", "indwxaa7"));
        decodedPost.Subject.ShouldBe("Plans");
        decodedPost.ReplyToId.ShouldBeNull();
        decodedPost.WebUrl.ShouldBeNull();
        TalkMessageCodec.ToChatMessage(reply, channel, Identify)!.ReplyToId.ShouldBe("100");

        // A chat has no replies or subjects, even when Talk has a thread.
        TalkMessageCodec.ToChatMessage(reply, Chat, Identify)!.ReplyToId.ShouldBeNull();
        TalkMessageCodec.ToChatMessage(post, Chat, Identify)!.Subject.ShouldBeNull();
    }

    [Fact]
    public void A_channel_mention_of_everyone_names_the_channel()
    {
        var channel = new TalkConversation("indwxaa7", "General") { Channel = new ChannelIdentity("team1", "indwxaa7") };
        var talk = new TalkMessage(9, "indwxaa7", new TalkActor("users", "alice", "Alice"), DateTimeOffset.UnixEpoch)
        {
            Text = "{mention-call1}",
            Parameters = new Dictionary<string, TalkParameter> { ["mention-call1"] = new("call", "indwxaa7", "General") },
        };

        TalkMessageCodec.ToChatMessage(talk, channel, Identify)!.Mentions.Single().Mentioned
            .ShouldBe(new ChatMentioned.Conversation("indwxaa7", "General", "channel"));
    }

    [Fact]
    public void The_people_a_batch_names_are_collected_for_one_lookup()
    {
        var withReactions = History.Select(m => m.Id == 59 ? m with { Reactions = [new TalkReaction("👍", new TalkActor("users", "carol", null), DateTimeOffset.UnixEpoch)] } : m);

        TalkMessageCodec.AccountsIn(withReactions).Distinct().Order().ShouldBe(["alice", "bob", "carol"]);
    }

    [Fact]
    public void A_text_body_is_sent_as_it_is_and_html_is_converted_with_mentions()
    {
        TalkMessageCodec.ToTalkText(new ChatMessageBody(ChatBodyType.Text, "<b>as is</b>"), new Dictionary<int, string>()).ShouldBe("<b>as is</b>");
        TalkMessageCodec.ToTalkText(new ChatMessageBody(ChatBodyType.Html, "<p>Hi <at id=\"0\">Bob</at></p>"), new Dictionary<int, string> { [0] = TalkMessageCodec.MentionOf("bob") })
            .ShouldBe("Hi @\"bob\"");
        TalkMessageCodec.MentionOf("we\"ird").ShouldBe("@\"weird\"");
    }

    private sealed class EventDetailComparer : IEqualityComparer<ChatEventDetail?>
    {
        public bool Equals(ChatEventDetail? x, ChatEventDetail? y) =>
            JsonSerializer.Serialize(x, x?.GetType() ?? typeof(object)) == JsonSerializer.Serialize(y, y?.GetType() ?? typeof(object));

        public int GetHashCode(ChatEventDetail? obj) => 0;
    }
}

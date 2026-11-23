using Nightware.Application.Chats;
using Nightware.Conformance.Tests.Harness;
using Nightware.Domain.Chats;
using Nightware.Domain.Kernel;

namespace Nightware.Conformance.Tests.Arrangements;

/// <summary>
/// Phase 23: chats over Nextcloud Talk (ADR 0009 D3–D4). The fixtures are constructed from Graph's reference pages with
/// Talk-shaped ids (room tokens, decimal message ids, <c>m.</c> plus the attendee id); <c>cloud.example.com</c> is Talk's
/// public URL, so <c>webUrl</c>s sit there.
/// </summary>
public sealed class ChatArrangements : IConformanceArrangements
{
    private const string GroupId = "5opkrcg6";
    private const string OneOnOneId = "nge9dkks";
    private const string AliceId = "8b081ef6-4792-4def-b2c9-c363a1bf41d5";
    private const string BobId = "5d3f2a1b-9c4e-4b7a-8f0d-2e6c1a9b7d44";
    private const string Web = "https://cloud.example.com/call/";

    private static readonly DateTimeOffset Created = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Sent = new(2026, 11, 21, 10, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = new(2026, 11, 21, 10, 35, 0, TimeSpan.Zero);

    private static readonly ChatIdentity Alice = new(AliceId, "Alice Anderson");
    private static readonly ChatIdentity Bob = new(BobId, "Bob Brown");

    private static readonly Chat Group = new(GroupId, ChatType.Group)
    {
        Topic = "Project review",
        LastUpdatedDateTime = Later,
        WebUrl = Web + GroupId,
        Viewpoint = new ChatViewpoint(false, Later),
    };

    private static readonly Chat OneOnOne = new(OneOnOneId, ChatType.OneOnOne)
    {
        LastUpdatedDateTime = Sent,
        WebUrl = Web + OneOnOneId,
        Viewpoint = new ChatViewpoint(true, null),
    };

    private static readonly ChatMember AliceMember = new("m.17", "Alice Anderson")
    {
        UserId = AliceId, Email = "alice@example.com", Roles = [ChatMember.OwnerRole],
    };

    private static readonly ChatMember BobMember = new("m.18", "Bob Brown") { UserId = BobId, Email = "bob@example.com" };

    private static readonly ChatMessage Added = Message("57", Created) with
    {
        MessageType = ChatMessageType.SystemEventMessage,
        From = null,
        Body = ChatMessageBody.SystemEvent,
        EventDetail = new ChatEventDetail.MembersAdded(Alice, [Bob]),
    };

    private static readonly ChatMessage Mention = Message("59", Sent) with
    {
        LastEditedDateTime = Later,
        LastModifiedDateTime = Later,
        Body = new ChatMessageBody(ChatBodyType.Html, "<p>Hello <strong>world</strong>, <at id=\"0\">Bob Brown</at></p>"),
        Mentions = [new ChatMention(0, "Bob Brown", new ChatMentioned.User(Bob))],
        Reactions = [new ChatReaction("👍", Later, Bob)],
    };

    private static readonly ChatMessage File = Message("76", Later) with
    {
        Body = new ChatMessageBody(ChatBodyType.Html, "<p><attachment id=\"467\"></attachment></p>"),
        Attachments = [new ChatMessageAttachment("467", ChatMessageAttachment.Reference, "https://cloud.example.com/f/467", "notes.txt")],
    };

    private static readonly ChatMessage Reply = Message("80", Later) with
    {
        Body = new ChatMessageBody(ChatBodyType.Html, "<p>Thanks <at id=\"0\">Bob Brown</at></p>"),
        Mentions = [new ChatMention(0, "Bob Brown", new ChatMentioned.User(Bob))],
    };

    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        yield return Arrange("chats.list", static f => f.For<IChatStore>()
            .ListAsync(Arg.Is<ChatPageQuery>(q => q.Top == 2 && q.SkipToken == null && !q.WithLastMessage), Any)
            .Returns(new Page<Chat>([Group, OneOnOne], "2")));
        yield return Arrange("chats.get", static f => f.For<IChatStore>().GetAsync(GroupId, Any).Returns(Group));
        yield return Arrange("chats.create", static f => f.For<IChatStore>()
            .CreateAsync(Arg.Is<ChatCreation>(c => c.ChatType == ChatType.Group && c.Topic == "Project review" && c.Members.Count == 1 && c.Members[0].User == BobId && c.Members[0].IsOwner), Any)
            .Returns(Group with { Viewpoint = new ChatViewpoint(false, null), LastUpdatedDateTime = Created }));
        yield return Arrange("chats.update", static f => f.For<IChatStore>()
            .RenameAsync(GroupId, "Project review (Q4)", Any).Returns(Group with { Topic = "Project review (Q4)" }));
        yield return Arrange("chats.hide", static f => f.For<IChatStore>().SetHiddenAsync(GroupId, true, Any).Returns(Result.Success()));
        yield return Arrange("chats.unhide", static f => f.For<IChatStore>().SetHiddenAsync(GroupId, false, Any).Returns(Result.Success()));
        yield return Arrange("chats.mark-read", static f => f.For<IChatStore>().MarkReadAsync(GroupId, Any).Returns(Result.Success()));
        yield return Arrange("chats.mark-unread", static f => f.For<IChatStore>().MarkUnreadAsync(GroupId, Any).Returns(Result.Success()));

        yield return Arrange("chats.members.list", static f => f.For<IChatMembers>()
            .ListAsync(GroupId, Any).Returns(Result<IReadOnlyList<ChatMember>>.Success([AliceMember, BobMember])));
        yield return Arrange("chats.members.get", static f => f.For<IChatMembers>().GetAsync(GroupId, "m.18", Any).Returns(BobMember));
        yield return Arrange("chats.members.add", static f => f.For<IChatMembers>()
            .AddAsync(GroupId, Arg.Is<ChatMemberDraft>(m => m.User == BobId && m.IsOwner), Any)
            .Returns(BobMember with { Id = "m.19", Roles = [ChatMember.OwnerRole] }));
        yield return Arrange("chats.members.remove", static f => f.For<IChatMembers>().RemoveAsync(GroupId, "m.18", Any).Returns(Result.Success()));

        yield return Arrange("chats.messages.list", static f => f.For<IChatMessages>()
            .ListAsync(GroupId, Arg.Is<ChatPageQuery>(q => q.Top == 3 && q.SkipToken == null), Any)
            .Returns(new Page<ChatMessage>([File, Mention, Added], "57")));
        yield return Arrange("chats.messages.get", static f => f.For<IChatMessages>().GetAsync(GroupId, "59", Any).Returns(Mention));
        yield return Arrange("chats.messages.send", static f => f.For<IChatMessages>()
            .SendAsync(GroupId, Arg.Is<ChatMessageDraft>(d => d.Body.ContentType == ChatBodyType.Html && d.Body.Content == "Thanks <at id=\"0\">Bob Brown</at>"
                                                             && d.Mentions.Count == 1 && d.Mentions[0].User == BobId), Any)
            .Returns(Reply));
        yield return Arrange("chats.messages.update", static f => f.For<IChatMessages>()
            .UpdateAsync(GroupId, "59", Arg.Is<ChatMessageDraft>(d => d.Body.Content == "<p>Updated <b>text</b></p>"), Any).Returns(Result.Success()));
        yield return Arrange("chats.messages.soft-delete", static f => f.For<IChatMessages>().SoftDeleteAsync(GroupId, "59", Any).Returns(Result.Success()));
        yield return Arrange("chats.messages.set-reaction", static f => f.For<IChatMessages>().SetReactionAsync(GroupId, "59", "💘", Any).Returns(Result.Success()));
        yield return Arrange("chats.messages.unset-reaction", static f => f.For<IChatMessages>().UnsetReactionAsync(GroupId, "59", "💘", Any).Returns(Result.Success()));

        yield return Arrange("chats.pinned.list", static f => f.For<IChatPins>()
            .ListAsync(GroupId, Any).Returns(Result<IReadOnlyList<PinnedChatMessage>>.Success([new PinnedChatMessage("59", Mention)])));
        yield return Arrange("chats.pinned.add", static f => f.For<IChatPins>().PinAsync(GroupId, "76", Any).Returns(new PinnedChatMessage("76", File)));
        yield return Arrange("chats.pinned.remove", static f => f.For<IChatPins>().UnpinAsync(GroupId, "59", Any).Returns(Result.Success()));
    }

    private static CancellationToken Any => Arg.Any<CancellationToken>();

    private static ChatMessage Message(string id, DateTimeOffset at) => new(id, ChatMessageType.Message, at)
    {
        ChatId = GroupId,
        From = Alice,
        LastModifiedDateTime = at,
        WebUrl = $"{Web}{GroupId}#message_{id}",
    };

    private static ConformanceArrangement Arrange(string operation, Action<PortFakes> arrange) => new(operation, arrange);
}

using Microsoft.Extensions.DependencyInjection;

using Nightware.Api.Discovery;
using Nightware.Application.Chats;
using Nightware.Application.Files.Drives;
using Nightware.Application.Teams;
using Nightware.Conformance.Tests.Harness;
using Nightware.Domain.Chats;
using Nightware.Domain.Files;
using Nightware.Domain.Kernel;
using Nightware.Domain.Teams;

namespace Nightware.Conformance.Tests.Arrangements;

/// <summary>
/// Phase 24: teams and channels over Nextcloud Teams and Talk (ADR 0009 D5). The fixtures are constructed from Graph's
/// reference pages with Nextcloud-shaped ids (a 31-character circle id, Talk room tokens, decimal message ids, <c>m.</c>
/// plus the circle member id); <c>cloud.example.com</c> is Talk's public URL and the file site.
/// </summary>
public sealed class TeamArrangements : IConformanceArrangements
{
    private const string TeamId = "Pq7Xk2LmN9vRt4WzYc8BdHf3JsQa6Ue";
    private const string GeneralId = "a7xk2mqp";
    private const string DesignId = "hktd6xvp";
    private const string AliceMembership = "m.LZ3QVwCD7nuUSNFdJYckPyiZN8OnTjH";
    private const string BobMembership = "m.zjioHBheH4iNehqhIX2yQrHrn6RVDCd";
    private const string AliceId = "8b081ef6-4792-4def-b2c9-c363a1bf41d5";
    private const string BobId = "5d3f2a1b-9c4e-4b7a-8f0d-2e6c1a9b7d44";
    private const string Web = "https://cloud.example.com/call/";

    private static readonly DateTimeOffset Created = new(2026, 11, 20, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Sent = new(2026, 11, 21, 10, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = new(2026, 11, 21, 10, 35, 0, TimeSpan.Zero);

    private static readonly ChatIdentity Alice = new(AliceId, "Alice Anderson");
    private static readonly ChatIdentity Bob = new(BobId, "Bob Brown");

    private static readonly Team Phoenix = new(TeamId, "Project Phoenix")
    {
        Description = "Planning the Phoenix launch",
        Visibility = TeamVisibility.Private,
        CreatedDateTime = Created,
        WebUrl = $"https://cloud.example.com/apps/circles/teams/team/{TeamId}",
    };

    private static readonly ChatMember AliceMember = new(AliceMembership, "Alice Anderson")
    {
        UserId = AliceId, Email = "alice@example.com", Roles = [ChatMember.OwnerRole],
    };

    private static readonly ChatMember BobMember = new(BobMembership, "Bob Brown") { UserId = BobId, Email = "bob@example.com" };

    private static readonly Channel General = new(GeneralId, "General") { WebUrl = Web + GeneralId, IsPrimary = true };

    private static readonly Channel Design = new(DesignId, "Design") { Description = "UI and UX work", WebUrl = Web + DesignId };

    private static readonly ChatMessage Post = Message("100", Sent, Alice) with
    {
        Subject = "Kick-off",
        Body = new ChatMessageBody(ChatBodyType.Html, "<p>Agenda <strong>here</strong></p>"),
        Reactions = [new ChatReaction("👍", Later, Bob)],
    };

    private static readonly ChatMessage Untitled = Message("106", Later, Bob) with
    {
        Body = new ChatMessageBody(ChatBodyType.Html, "<p>Status update</p>"),
    };

    private static readonly ChatMessage Added = Message("98", Created, null) with
    {
        MessageType = ChatMessageType.SystemEventMessage,
        Body = ChatMessageBody.SystemEvent,
        EventDetail = new ChatEventDetail.MembersAdded(Alice, [Bob]),
    };

    private static readonly ChatMessage Reply = Message("102", Later, Bob) with
    {
        ReplyToId = "100",
        Body = new ChatMessageBody(ChatBodyType.Html, "<p>Sounds good</p>"),
    };

    private static readonly ChatMessage Thanks = Message("104", Later, Alice) with
    {
        ReplyToId = "100",
        Body = new ChatMessageBody(ChatBodyType.Html, "<p>Thanks <at id=\"0\">Bob Brown</at></p>"),
        Mentions = [new ChatMention(0, "Bob Brown", new ChatMentioned.User(Bob))],
    };

    private static readonly DriveItem DesignFolder = new("512", "Design")
    {
        DriveId = "nc!YWxpY2U", IsFolder = true, ChildCount = 0, Size = 0, ETag = "e-512", ParentId = "490", ParentPath = "/Project Phoenix",
        CreatedDateTime = Created, LastModifiedDateTime = Sent,
    };

    private static readonly ChannelMessageRef PostRef = new(TeamId, DesignId, "100");
    private static readonly ChannelMessageRef ReplyRef = new(TeamId, DesignId, "100", "102");

    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        yield return Arrange("teams.joined", static f => f.For<ITeamStore>().ListJoinedAsync(Any).Returns(Result<IReadOnlyList<Team>>.Success([Phoenix])));
        yield return Arrange("teams.get", static f => f.For<ITeamStore>().GetAsync(TeamId, Any).Returns(Phoenix));
        yield return Arrange("teams.create", static f => f.For<ITeamStore>()
            .CreateAsync(Arg.Is<TeamCreation>(c => c.DisplayName == "Project Phoenix" && c.Description == "Planning the Phoenix launch" && c.Visibility == null
                                                    && c.Members.Count == 1 && c.Members[0].User == BobId && c.Members[0].IsOwner), Any)
            .Returns(Phoenix));
        yield return Arrange("teams.operation.get", static f => f.For<ITeamStore>().GetAsync(TeamId, Any).Returns(Phoenix));
        yield return Arrange("teams.update", static f => f.For<ITeamStore>()
            .UpdateAsync(TeamId, Arg.Is<TeamUpdate>(u => u.DisplayName == "Project Phoenix (2027)" && u.Description == "Planning the launch" && u.Visibility == TeamVisibility.Public), Any)
            .Returns(Result.Success()));
        yield return Arrange("teams.delete", static f => f.For<ITeamStore>().DeleteAsync(TeamId, Any).Returns(Result.Success()));

        yield return Arrange("teams.members.list", static f => f.For<ITeamMembers>()
            .ListAsync(TeamId, Any).Returns(Result<IReadOnlyList<ChatMember>>.Success([AliceMember, BobMember])));
        yield return Arrange("teams.members.get", static f => f.For<ITeamMembers>().GetAsync(TeamId, BobMembership, Any).Returns(BobMember));
        yield return Arrange("teams.members.add", static f => f.For<ITeamMembers>()
            .AddAsync(TeamId, Arg.Is<ChatMemberDraft>(m => m.User == BobId && m.IsOwner), Any)
            .Returns(BobMember with { Roles = [ChatMember.OwnerRole] }));
        yield return Arrange("teams.members.update", static f => f.For<ITeamMembers>()
            .SetOwnerAsync(TeamId, BobMembership, true, Any).Returns(BobMember with { Roles = [ChatMember.OwnerRole] }));
        yield return Arrange("teams.members.remove", static f => f.For<ITeamMembers>().RemoveAsync(TeamId, BobMembership, Any).Returns(Result.Success()));

        yield return Arrange("channels.list", static f => f.For<IChannelStore>()
            .ListAsync(TeamId, Any).Returns(Result<IReadOnlyList<Channel>>.Success([General, Design])));
        yield return Arrange("channels.get", static f => f.For<IChannelStore>().GetAsync(TeamId, DesignId, Any).Returns(Design));
        yield return Arrange("channels.primary", static f => f.For<IChannelStore>().GetPrimaryAsync(TeamId, Any).Returns(General));
        yield return Arrange("channels.create", static f => f.For<IChannelStore>()
            .CreateAsync(TeamId, Arg.Is<ChannelCreation>(c => c.DisplayName == "Design" && c.Description == "UI and UX work"), Any)
            .Returns(Design));
        yield return Arrange("channels.update", static f => f.For<IChannelStore>()
            .UpdateAsync(TeamId, DesignId, Arg.Is<ChannelUpdate>(u => u.DisplayName == "Design team" && u.Description == "UI, UX and brand work"), Any)
            .Returns(Result.Success()));
        yield return Arrange("channels.delete", static f => f.For<IChannelStore>().DeleteAsync(TeamId, DesignId, Any).Returns(Result.Success()));
        yield return new ConformanceArrangement("channels.files-folder", static f =>
        {
            f.For<IChannelStore>().FilesFolderPathAsync(TeamId, DesignId, Any).Returns(Result<IReadOnlyList<string>>.Success(["Project Phoenix", "Design"]));
            f.For<IDriveStore>().GetItemAsync(Arg.Is<DriveItemRef>(r => r.ItemId == null && r.Path.SequenceEqual(new[] { "Project Phoenix", "Design" })), Any)
                .Returns(DesignFolder);
        })
        {
            Services = static services => services.PostConfigure<DiscoveryOptions>(static o => o.FilesBaseUrls = ["https://cloud.example.com"]),
        };
        yield return Arrange("channels.members.list", static f =>
        {
            f.For<IChannelStore>().GetAsync(TeamId, DesignId, Any).Returns(Design);
            f.For<ITeamMembers>().ListAsync(TeamId, Any).Returns(Result<IReadOnlyList<ChatMember>>.Success([AliceMember, BobMember]));
        });

        yield return Arrange("channels.messages.list", static f => f.For<IChannelMessages>()
            .ListPostsAsync(TeamId, DesignId, Arg.Is<ChatPageQuery>(q => q.Top == 3 && q.SkipToken == null), Any)
            .Returns(new Page<ChatMessage>([Untitled, Post, Added], "98")));
        yield return Arrange("channels.messages.get", static f => f.For<IChannelMessages>().GetAsync(PostRef, Any).Returns(Post));
        yield return Arrange("channels.messages.send", static f => f.For<IChannelMessages>()
            .PostAsync(TeamId, DesignId, Arg.Is<ChatMessageDraft>(d => d.Subject == "Kick-off" && d.Body.ContentType == ChatBodyType.Html && d.Body.Content == "Agenda <b>here</b>"), Any)
            .Returns(Post with { Reactions = [] }));
        yield return Arrange("channels.messages.update", static f => f.For<IChannelMessages>()
            .UpdateAsync(PostRef, Arg.Is<ChatMessageDraft>(d => d.Subject == "Kick-off (moved)" && d.Body.Content == "Agenda <b>moved</b>"), Any)
            .Returns(Result.Success()));
        yield return Arrange("channels.messages.soft-delete", static f => f.For<IChannelMessages>().SoftDeleteAsync(PostRef, Any).Returns(Result.Success()));
        yield return Arrange("channels.messages.set-reaction", static f => f.For<IChannelMessages>().SetReactionAsync(PostRef, "💘", Any).Returns(Result.Success()));
        yield return Arrange("channels.messages.unset-reaction", static f => f.For<IChannelMessages>().UnsetReactionAsync(PostRef, "💘", Any).Returns(Result.Success()));
        yield return Arrange("channels.messages.delta", static f => f.For<IChannelMessages>()
            .DeltaAsync(TeamId, DesignId, null, ChatLimits.DefaultPageSize, Any)
            .Returns(DeltaPage<ChatMessage>.Done([Upsert(Untitled), Upsert(Post), Upsert(Added)], new DeltaToken("t"))));

        yield return Arrange("channels.replies.list", static f => f.For<IChannelMessages>()
            .ListRepliesAsync(PostRef, Arg.Is<ChatPageQuery>(q => q.Top == ChatLimits.DefaultPageSize && q.SkipToken == null), Any)
            .Returns(new Page<ChatMessage>([Thanks, Reply])));
        yield return Arrange("channels.replies.get", static f => f.For<IChannelMessages>().GetAsync(ReplyRef, Any).Returns(Reply));
        yield return Arrange("channels.replies.send", static f => f.For<IChannelMessages>()
            .ReplyAsync(PostRef, Arg.Is<ChatMessageDraft>(d => d.Body.Content == "Sounds good" && d.Subject == null), Any)
            .Returns(Reply));
        yield return Arrange("channels.replies.update", static f => f.For<IChannelMessages>()
            .UpdateAsync(ReplyRef, Arg.Is<ChatMessageDraft>(d => d.Body.Content == "Sounds <i>very</i> good"), Any).Returns(Result.Success()));
        yield return Arrange("channels.replies.soft-delete", static f => f.For<IChannelMessages>().SoftDeleteAsync(ReplyRef, Any).Returns(Result.Success()));
        yield return Arrange("channels.replies.set-reaction", static f => f.For<IChannelMessages>().SetReactionAsync(ReplyRef, "👍", Any).Returns(Result.Success()));
        yield return Arrange("channels.replies.unset-reaction", static f => f.For<IChannelMessages>().UnsetReactionAsync(ReplyRef, "👍", Any).Returns(Result.Success()));
    }

    private static CancellationToken Any => Arg.Any<CancellationToken>();

    private static DeltaEntry<ChatMessage> Upsert(ChatMessage message) => new DeltaEntry<ChatMessage>.Upsert(message);

    private static ChatMessage Message(string id, DateTimeOffset at, ChatIdentity? from) => new(id, ChatMessageType.Message, at)
    {
        ChannelIdentity = new ChannelIdentity(TeamId, DesignId),
        From = from,
        LastModifiedDateTime = at,
        WebUrl = $"{Web}{DesignId}#message_{id}",
    };

    private static ConformanceArrangement Arrange(string operation, Action<PortFakes> arrange) => new(operation, arrange);
}

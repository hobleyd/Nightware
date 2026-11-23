using Nightware.Application.Chats;
using Nightware.Application.Teams;
using Nightware.Domain.Chats;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Teams;

public sealed class ChannelMessageUseCaseTests
{
    private const string TeamId = "6LOrxifQ5p4vRt2I3iplImWqdpjlADT";
    private const string ChannelId = "xtugjbpy";
    private static readonly ChannelMessageRef Post = new(TeamId, ChannelId, "209");
    private static readonly ChannelMessageRef ReplyRef = new(TeamId, ChannelId, "209", "212");
    private static readonly ChatMessage PostMessage = new("209", ChatMessageType.Message, DateTimeOffset.UnixEpoch) { Subject = "Kick-off" };
    private static readonly ChatMessage ReplyMessage = new("212", ChatMessageType.Message, DateTimeOffset.UnixEpoch) { ReplyToId = "209" };
    private static readonly ChatMessageDraft Draft = new(new ChatMessageBody(ChatBodyType.Html, "<p>Hi</p>"));

    private readonly IChannelMessages _messages = Substitute.For<IChannelMessages>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_subject_has_a_length_limit_and_may_be_absent()
    {
        ChannelMessageRules.Subject(null).ShouldBeNull();
        ChannelMessageRules.Subject("Kick-off").ShouldBeNull();
        ChannelMessageRules.Subject(" ")!.Target.ShouldBe("subject");
        ChannelMessageRules.Subject(new string('x', ChannelMessageRules.MaxSubjectLength + 1))!.Target.ShouldBe("subject");
    }

    [Fact]
    public async Task Listing_posts_checks_the_page_size()
    {
        (await new ListChannelMessages(_messages).ExecuteAsync(TeamId, ChannelId, new ChatPageQuery(51, null), false, Ct)).Error!.Target.ShouldBe("$top");
        await _messages.DidNotReceiveWithAnyArgs().ListPostsAsync(default!, default!, default!, Ct);
    }

    [Fact]
    public async Task Listing_posts_without_replies_reads_one_page()
    {
        _messages.ListPostsAsync(TeamId, ChannelId, Arg.Any<ChatPageQuery>(), Ct).Returns(new Page<ChatMessage>([PostMessage], "209"));

        var page = await new ListChannelMessages(_messages).ExecuteAsync(TeamId, ChannelId, new ChatPageQuery(20, null), false, Ct);

        page.Value.Items.Single().ShouldBe(new ChannelPostView(PostMessage));
        page.Value.SkipToken.ShouldBe("209");
        await _messages.DidNotReceiveWithAnyArgs().ListRepliesAsync(default!, default!, Ct);
    }

    [Fact]
    public async Task Listing_posts_with_replies_reads_each_posts_thread()
    {
        _messages.ListPostsAsync(TeamId, ChannelId, Arg.Any<ChatPageQuery>(), Ct).Returns(new Page<ChatMessage>([PostMessage]));
        _messages.ListRepliesAsync(Post, Arg.Is<ChatPageQuery>(q => q.Top == ChannelMessageRules.ExpandedReplies), Ct).Returns(new Page<ChatMessage>([ReplyMessage]));

        var page = await new ListChannelMessages(_messages).ExecuteAsync(TeamId, ChannelId, new ChatPageQuery(20, null), true, Ct);

        page.Value.Items.Single().Replies.ShouldBe([ReplyMessage]);
    }

    [Fact]
    public async Task Listing_fails_with_the_posts_or_a_thread()
    {
        _messages.ListPostsAsync(TeamId, "nochannel", Arg.Any<ChatPageQuery>(), Ct).Returns(Error.NotFound("no"));
        (await new ListChannelMessages(_messages).ExecuteAsync(TeamId, "nochannel", new ChatPageQuery(20, null), true, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);

        _messages.ListPostsAsync(TeamId, ChannelId, Arg.Any<ChatPageQuery>(), Ct).Returns(new Page<ChatMessage>([PostMessage]));
        _messages.ListRepliesAsync(Post, Arg.Any<ChatPageQuery>(), Ct).Returns(Error.BackendUnavailable("down"));
        (await new ListChannelMessages(_messages).ExecuteAsync(TeamId, ChannelId, new ChatPageQuery(20, null), true, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task Listing_replies_checks_the_page_size_then_reads()
    {
        _messages.ListRepliesAsync(Post, Arg.Any<ChatPageQuery>(), Ct).Returns(new Page<ChatMessage>([ReplyMessage]));

        (await new ListChannelMessageReplies(_messages).ExecuteAsync(Post, new ChatPageQuery(0, null), Ct)).Error!.Target.ShouldBe("$top");
        (await new ListChannelMessageReplies(_messages).ExecuteAsync(Post, new ChatPageQuery(10, null), Ct)).Value.Items.ShouldBe([ReplyMessage]);
    }

    [Fact]
    public async Task Getting_a_post_or_a_reply_passes_through()
    {
        _messages.GetAsync(Post, Ct).Returns(PostMessage);
        _messages.GetAsync(ReplyRef, Ct).Returns(ReplyMessage);

        (await new GetChannelMessage(_messages).ExecuteAsync(Post, Ct)).Value.ShouldBe(PostMessage);
        (await new GetChannelMessage(_messages).ExecuteAsync(ReplyRef, Ct)).Value.ShouldBe(ReplyMessage);
    }

    [Fact]
    public async Task Posting_checks_the_body_and_subject()
    {
        _messages.PostAsync(TeamId, ChannelId, Arg.Any<ChatMessageDraft>(), Ct).Returns(PostMessage);

        (await new SendChannelMessage(_messages).ExecuteAsync(TeamId, ChannelId, Draft with { Subject = "Kick-off" }, Ct)).Value.ShouldBe(PostMessage);
        (await new SendChannelMessage(_messages).ExecuteAsync(TeamId, ChannelId, new ChatMessageDraft(new ChatMessageBody(ChatBodyType.Text, " ")), Ct)).Error!.Target.ShouldBe("body");
        (await new SendChannelMessage(_messages).ExecuteAsync(TeamId, ChannelId, Draft with { Subject = new string('x', 300) }, Ct)).Error!.Target.ShouldBe("subject");
        await _messages.Received(1).PostAsync(TeamId, ChannelId, Arg.Any<ChatMessageDraft>(), Ct);
    }

    [Fact]
    public async Task A_reply_drops_any_subject()
    {
        _messages.ReplyAsync(Post, Arg.Is<ChatMessageDraft>(d => d.Subject == null), Ct).Returns(ReplyMessage);

        (await new SendChannelMessageReply(_messages).ExecuteAsync(Post, Draft with { Subject = "ignored" }, Ct)).Value.ShouldBe(ReplyMessage);
        (await new SendChannelMessageReply(_messages).ExecuteAsync(Post, new ChatMessageDraft(new ChatMessageBody(ChatBodyType.Text, "")), Ct)).Error!.Target.ShouldBe("body");
    }

    [Fact]
    public async Task Updating_checks_the_draft_and_a_reply_takes_no_subject()
    {
        _messages.UpdateAsync(Arg.Any<ChannelMessageRef>(), Arg.Any<ChatMessageDraft>(), Ct).Returns(Result.Success());

        (await new UpdateChannelMessage(_messages).ExecuteAsync(Post, Draft with { Subject = "New" }, Ct)).IsSuccess.ShouldBeTrue();
        (await new UpdateChannelMessage(_messages).ExecuteAsync(ReplyRef, Draft, Ct)).IsSuccess.ShouldBeTrue();
        (await new UpdateChannelMessage(_messages).ExecuteAsync(ReplyRef, Draft with { Subject = "New" }, Ct)).Error!.Target.ShouldBe("subject");
        (await new UpdateChannelMessage(_messages).ExecuteAsync(Post, Draft with { Subject = " " }, Ct)).Error!.Target.ShouldBe("subject");
        await _messages.Received(2).UpdateAsync(Arg.Any<ChannelMessageRef>(), Arg.Any<ChatMessageDraft>(), Ct);
    }

    [Fact]
    public async Task Deleting_and_reacting_pass_through_with_reactions_as_emoji()
    {
        _messages.SoftDeleteAsync(ReplyRef, Ct).Returns(Result.Success());
        _messages.SetReactionAsync(Post, "👍", Ct).Returns(Result.Success());
        _messages.UnsetReactionAsync(ReplyRef, "❤️", Ct).Returns(Result.Success());

        (await new SoftDeleteChannelMessage(_messages).ExecuteAsync(ReplyRef, Ct)).IsSuccess.ShouldBeTrue();
        (await new SetChannelMessageReaction(_messages).ExecuteAsync(Post, "like", Ct)).IsSuccess.ShouldBeTrue();
        (await new UnsetChannelMessageReaction(_messages).ExecuteAsync(ReplyRef, "heart", Ct)).IsSuccess.ShouldBeTrue();
        (await new SetChannelMessageReaction(_messages).ExecuteAsync(Post, " ", Ct)).Error!.Target.ShouldBe("reactionType");
        (await new UnsetChannelMessageReaction(_messages).ExecuteAsync(Post, null, Ct)).Error!.Target.ShouldBe("reactionType");
    }

    [Fact]
    public async Task The_delta_takes_the_default_page_size_or_a_checked_one()
    {
        var page = DeltaPage<ChatMessage>.Done([new DeltaEntry<ChatMessage>.Upsert(PostMessage)], new DeltaToken("t"));
        _messages.DeltaAsync(TeamId, ChannelId, null, ChatLimits.DefaultPageSize, Ct).Returns(page);
        _messages.DeltaAsync(TeamId, ChannelId, new DeltaToken("t"), 5, Ct).Returns(page);

        (await new GetChannelMessagesDelta(_messages).ExecuteAsync(TeamId, ChannelId, null, null, Ct)).Value.ShouldBe(page);
        (await new GetChannelMessagesDelta(_messages).ExecuteAsync(TeamId, ChannelId, new DeltaToken("t"), 5, Ct)).Value.ShouldBe(page);
        (await new GetChannelMessagesDelta(_messages).ExecuteAsync(TeamId, ChannelId, null, 51, Ct)).Error!.Target.ShouldBe("$top");
    }
}

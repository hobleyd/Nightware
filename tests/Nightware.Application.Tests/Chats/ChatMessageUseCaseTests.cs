using Nightware.Application.Chats;
using Nightware.Domain.Chats;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Chats;

public sealed class ChatMessageUseCaseTests
{
    private const string ChatId = "abcd1234";
    private static readonly ChatMessage Message = new("59", ChatMessageType.Message, DateTimeOffset.UnixEpoch);
    private static readonly ChatMessageDraft Draft = new(new ChatMessageBody(ChatBodyType.Html, "<p>Hi</p>"));

    private readonly IChatMessages _messages = Substitute.For<IChatMessages>();
    private readonly IChatPins _pins = Substitute.For<IChatPins>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_draft_needs_a_body_within_the_limit()
    {
        ChatMessageRules.Validate(Draft).ShouldBeNull();
        ChatMessageRules.Validate(new ChatMessageDraft(new ChatMessageBody(ChatBodyType.Text, "  ")))!.Target.ShouldBe("body");
        ChatMessageRules.Validate(new ChatMessageDraft(new ChatMessageBody(ChatBodyType.Text, new string('x', ChatMessageRules.MaxBodyLength + 1))))!.Target.ShouldBe("body");
    }

    [Fact]
    public void Mentions_need_distinct_ids_and_a_user_or_the_conversation()
    {
        ChatMessageRules.Validate(Draft with { Mentions = [new ChatMentionDraft(0, "Bob", "bob-id"), new ChatMentionDraft(1, null, null, true)] }).ShouldBeNull();
        ChatMessageRules.Validate(Draft with { Mentions = [new ChatMentionDraft(0, "Bob", "bob-id"), new ChatMentionDraft(0, "Carol", "carol-id")] })!.Target.ShouldBe("mentions");
        ChatMessageRules.Validate(Draft with { Mentions = [new ChatMentionDraft(0, "Bob", null)] })!.Target.ShouldBe("mentions");
    }

    [Theory]
    [InlineData("like", "👍")]
    [InlineData("HEART", "❤️")]
    [InlineData("laugh", "😆")]
    [InlineData("surprised", "😮")]
    [InlineData("sad", "😢")]
    [InlineData("angry", "😡")]
    [InlineData(" 🎉 ", "🎉")]
    public void Reactions_are_emoji_and_legacy_names_map_to_them(string given, string emoji) =>
        ChatMessageRules.Reaction(given).Value.ShouldBe(emoji);

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public void A_reaction_must_be_given(string? given) =>
        ChatMessageRules.Reaction(given).Error!.Target.ShouldBe("reactionType");

    [Fact]
    public void An_overlong_reaction_is_refused() =>
        ChatMessageRules.Reaction(new string('x', 65)).IsSuccess.ShouldBeFalse();

    [Fact]
    public async Task List_checks_the_page_size_then_reads()
    {
        (await new ListChatMessages(_messages).ExecuteAsync(ChatId, new ChatPageQuery(51, null), Ct)).Error!.Target.ShouldBe("$top");
        await _messages.DidNotReceiveWithAnyArgs().ListAsync(default!, default!, Ct);

        var query = new ChatPageQuery(2, "60");
        _messages.ListAsync(ChatId, query, Ct).Returns(new Page<ChatMessage>([Message], "59"));
        (await new ListChatMessages(_messages).ExecuteAsync(ChatId, query, Ct)).Value.SkipToken.ShouldBe("59");
    }

    [Fact]
    public async Task Get_soft_delete_and_unpin_pass_through()
    {
        _messages.GetAsync(ChatId, "59", Ct).Returns(Message);
        _messages.SoftDeleteAsync(ChatId, "59", Ct).Returns(Result.Success());
        _pins.UnpinAsync(ChatId, "59", Ct).Returns(Error.NotFound("gone"));

        (await new GetChatMessage(_messages).ExecuteAsync(ChatId, "59", Ct)).Value.ShouldBeSameAs(Message);
        (await new SoftDeleteChatMessage(_messages).ExecuteAsync(ChatId, "59", Ct)).IsSuccess.ShouldBeTrue();
        (await new UnpinChatMessage(_pins).ExecuteAsync(ChatId, "59", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Send_and_update_validate_the_draft_first()
    {
        var empty = new ChatMessageDraft(new ChatMessageBody(ChatBodyType.Text, ""));
        (await new SendChatMessage(_messages).ExecuteAsync(ChatId, empty, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await new UpdateChatMessage(_messages).ExecuteAsync(ChatId, "59", empty, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        await _messages.DidNotReceiveWithAnyArgs().SendAsync(default!, default!, Ct);
        await _messages.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default!, default!, Ct);

        _messages.SendAsync(ChatId, Draft, Ct).Returns(Message);
        _messages.UpdateAsync(ChatId, "59", Draft, Ct).Returns(Result.Success());
        (await new SendChatMessage(_messages).ExecuteAsync(ChatId, Draft, Ct)).Value.ShouldBeSameAs(Message);
        (await new UpdateChatMessage(_messages).ExecuteAsync(ChatId, "59", Draft, Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Reactions_are_sent_as_emoji()
    {
        _messages.SetReactionAsync(ChatId, "59", "👍", Ct).Returns(Result.Success());
        _messages.UnsetReactionAsync(ChatId, "59", "❤️", Ct).Returns(Result.Success());

        (await new SetChatMessageReaction(_messages).ExecuteAsync(ChatId, "59", "like", Ct)).IsSuccess.ShouldBeTrue();
        (await new UnsetChatMessageReaction(_messages).ExecuteAsync(ChatId, "59", "heart", Ct)).IsSuccess.ShouldBeTrue();
        (await new SetChatMessageReaction(_messages).ExecuteAsync(ChatId, "59", null, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
        (await new UnsetChatMessageReaction(_messages).ExecuteAsync(ChatId, "59", "", Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);
    }

    [Fact]
    public async Task Pins_list_and_pin_a_named_message()
    {
        var pinned = new PinnedChatMessage("59", Message);
        _pins.ListAsync(ChatId, Ct).Returns(Result<IReadOnlyList<PinnedChatMessage>>.Success([pinned]));
        _pins.PinAsync(ChatId, "59", Ct).Returns(pinned);

        (await new ListPinnedChatMessages(_pins).ExecuteAsync(ChatId, Ct)).Value.Single().ShouldBeSameAs(pinned);
        (await new PinChatMessage(_pins).ExecuteAsync(ChatId, "59", Ct)).Value.ShouldBeSameAs(pinned);
        (await new PinChatMessage(_pins).ExecuteAsync(ChatId, " ", Ct)).Error!.Target.ShouldBe("message@odata.bind");
    }
}

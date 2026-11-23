using Microsoft.Extensions.DependencyInjection;

using Nightware.Application.Chats;
using Nightware.Application.Common.Identity;
using Nightware.Domain.Chats;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Chats;

public sealed class ChatUseCaseTests
{
    private const string ChatId = "abcd1234";
    private static readonly Chat Group = new(ChatId, ChatType.Group) { Topic = "Team" };
    private static readonly ChatMember Bob = new("m.18", "Bob Brown") { UserId = "bob-id" };

    private readonly IChatStore _store = Substitute.For<IChatStore>();
    private readonly IChatMembers _members = Substitute.For<IChatMembers>();
    private readonly IUserContext _caller = Substitute.For<IUserContext>();

    public ChatUseCaseTests() =>
        _caller.Principal.Returns(new Principal("alice-id", "alice@example.com", "alice@example.com", "Alice"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    public void Page_size_out_of_range_is_refused(int top) =>
        ChatRules.PageSize(top).Error!.Target.ShouldBe("$top");

    [Fact]
    public void Page_size_defaults_and_passes_through()
    {
        ChatRules.PageSize(null).Value.ShouldBe(ChatLimits.DefaultPageSize);
        ChatRules.PageSize(7).Value.ShouldBe(7);
    }

    [Theory]
    [InlineData("alice-id", true)]
    [InlineData("ALICE@example.com", true)]
    [InlineData("bob-id", false)]
    [InlineData(null, false)]
    [InlineData(" ", false)]
    public void The_caller_is_named_by_id_upn_or_address(string? user, bool expected) =>
        ChatRules.IsCaller(new Principal("alice-id", "alice@example.com", "alice@example.com", "Alice"), user).ShouldBe(expected);

    [Fact]
    public async Task List_refuses_a_bad_page_size_before_the_store()
    {
        var result = await new ListChats(_store, _members).ExecuteAsync(new ChatPageQuery(99, null), false, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        await _store.DidNotReceiveWithAnyArgs().ListAsync(default!, Ct);
    }

    [Fact]
    public async Task List_without_members_reads_only_the_store()
    {
        var query = new ChatPageQuery(10, "5");
        _store.ListAsync(query, Ct).Returns(new Page<Chat>([Group], "15"));

        var page = (await new ListChats(_store, _members).ExecuteAsync(query, false, Ct)).Value;

        page.SkipToken.ShouldBe("15");
        page.Items.Single().Chat.ShouldBeSameAs(Group);
        page.Items.Single().Members.ShouldBeNull();
        await _members.DidNotReceiveWithAnyArgs().ListAsync(default!, Ct);
    }

    [Fact]
    public async Task List_with_members_expands_each_chat_and_fails_with_a_member_listing()
    {
        var query = new ChatPageQuery(10, null);
        _store.ListAsync(query, Ct).Returns(new Page<Chat>([Group]));
        _members.ListAsync(ChatId, Ct).Returns(Result<IReadOnlyList<ChatMember>>.Success([Bob]));

        (await new ListChats(_store, _members).ExecuteAsync(query, true, Ct)).Value.Items.Single().Members!.Single().ShouldBeSameAs(Bob);

        _members.ListAsync(ChatId, Ct).Returns(Error.Forbidden("no"));
        (await new ListChats(_store, _members).ExecuteAsync(query, true, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public async Task List_passes_a_store_failure_through()
    {
        _store.ListAsync(Arg.Any<ChatPageQuery>(), Ct).Returns(Error.BackendUnavailable("down"));

        (await new ListChats(_store, _members).ExecuteAsync(new ChatPageQuery(10, null), false, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public async Task Get_returns_the_chat_with_members_when_asked()
    {
        _store.GetAsync(ChatId, Ct).Returns(Group);
        _members.ListAsync(ChatId, Ct).Returns(Result<IReadOnlyList<ChatMember>>.Success([Bob]));

        (await new GetChat(_store, _members).ExecuteAsync(ChatId, false, Ct)).Value.Members.ShouldBeNull();
        (await new GetChat(_store, _members).ExecuteAsync(ChatId, true, Ct)).Value.Members!.Count.ShouldBe(1);

        _store.GetAsync("gone1234", Ct).Returns(Error.NotFound("gone"));
        (await new GetChat(_store, _members).ExecuteAsync("gone1234", true, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Create_one_on_one_drops_the_caller_and_passes_the_other()
    {
        var created = new Chat(ChatId, ChatType.OneOnOne);
        _store.CreateAsync(Arg.Is<ChatCreation>(c => c.Members.Count == 1 && c.Members[0].User == "bob-id"), Ct).Returns(created);

        var result = await new CreateChat(_store, _caller).ExecuteAsync(
            new ChatCreation(ChatType.OneOnOne, null, [new ChatMemberDraft("alice@example.com", true), new ChatMemberDraft("bob-id", true)]), Ct);

        result.Value.ShouldBeSameAs(created);
    }

    [Fact]
    public async Task Create_trims_the_topic_and_deduplicates_members()
    {
        _store.CreateAsync(Arg.Any<ChatCreation>(), Ct).Returns(Group);

        await new CreateChat(_store, _caller).ExecuteAsync(
            new ChatCreation(ChatType.Group, "  Team  ", [new ChatMemberDraft("bob-id", false), new ChatMemberDraft("BOB-ID", true), new ChatMemberDraft("carol-id", false)]), Ct);

        await _store.Received(1).CreateAsync(Arg.Is<ChatCreation>(c => c.Topic == "Team" && c.Members.Count == 2), Ct);
    }

    public static TheoryData<ChatCreation, string> InvalidCreations() => new()
    {
        { new ChatCreation(ChatType.Meeting, "x", [new ChatMemberDraft("bob-id", false)]), "chatType" },
        { new ChatCreation(ChatType.OneOnOne, null, [new ChatMemberDraft("alice-id", false)]), "members" },
        { new ChatCreation(ChatType.OneOnOne, null, [new ChatMemberDraft("bob-id", false), new ChatMemberDraft("carol-id", false)]), "members" },
        { new ChatCreation(ChatType.OneOnOne, "topic", [new ChatMemberDraft("bob-id", false)]), "topic" },
        { new ChatCreation(ChatType.Group, "x", [new ChatMemberDraft("alice-id", false)]), "members" },
        { new ChatCreation(ChatType.Group, "x", [new ChatMemberDraft(" ", false)]), "members" },
    };

    [Theory]
    [MemberData(nameof(InvalidCreations))]
    public async Task Create_refuses_what_graph_refuses(ChatCreation creation, string target)
    {
        var result = await new CreateChat(_store, _caller).ExecuteAsync(creation, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        result.Error.Target.ShouldBe(target);
        await _store.DidNotReceiveWithAnyArgs().CreateAsync(default!, Ct);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task Update_refuses_an_empty_topic(string? topic) =>
        (await new UpdateChat(_store).ExecuteAsync(ChatId, topic, Ct)).Error!.Target.ShouldBe("topic");

    [Fact]
    public async Task Update_renames_with_the_trimmed_topic()
    {
        _store.RenameAsync(ChatId, "New", Ct).Returns(Group);

        (await new UpdateChat(_store).ExecuteAsync(ChatId, " New ", Ct)).Value.ShouldBeSameAs(Group);
    }

    [Fact]
    public async Task For_user_actions_act_for_the_caller_only()
    {
        _store.SetHiddenAsync(ChatId, true, Ct).Returns(Result.Success());
        _store.SetHiddenAsync(ChatId, false, Ct).Returns(Result.Success());
        _store.MarkReadAsync(ChatId, Ct).Returns(Result.Success());
        _store.MarkUnreadAsync(ChatId, Ct).Returns(Result.Success());

        (await new HideChat(_store, _caller).ExecuteAsync(ChatId, "alice-id", Ct)).IsSuccess.ShouldBeTrue();
        (await new UnhideChat(_store, _caller).ExecuteAsync(ChatId, "alice-id", Ct)).IsSuccess.ShouldBeTrue();
        (await new MarkChatRead(_store, _caller).ExecuteAsync(ChatId, "alice-id", Ct)).IsSuccess.ShouldBeTrue();
        (await new MarkChatUnread(_store, _caller).ExecuteAsync(ChatId, "alice-id", Ct)).IsSuccess.ShouldBeTrue();

        (await new HideChat(_store, _caller).ExecuteAsync(ChatId, "bob-id", Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        (await new UnhideChat(_store, _caller).ExecuteAsync(ChatId, null, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        (await new MarkChatRead(_store, _caller).ExecuteAsync(ChatId, "bob-id", Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        (await new MarkChatUnread(_store, _caller).ExecuteAsync(ChatId, "bob-id", Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
        await _store.Received(1).SetHiddenAsync(ChatId, true, Ct);
        await _store.Received(1).MarkReadAsync(ChatId, Ct);
    }

    [Fact]
    public async Task Member_reads_and_removal_pass_through()
    {
        _members.ListAsync(ChatId, Ct).Returns(Result<IReadOnlyList<ChatMember>>.Success([Bob]));
        _members.GetAsync(ChatId, "m.18", Ct).Returns(Bob);
        _members.RemoveAsync(ChatId, "m.18", Ct).Returns(Result.Success());

        (await new ListChatMembers(_members).ExecuteAsync(ChatId, Ct)).Value.Single().ShouldBeSameAs(Bob);
        (await new GetChatMember(_members).ExecuteAsync(ChatId, "m.18", Ct)).Value.ShouldBeSameAs(Bob);
        (await new RemoveChatMember(_members).ExecuteAsync(ChatId, "m.18", Ct)).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task Adding_a_member_needs_a_user_and_trims_it()
    {
        (await new AddChatMember(_members).ExecuteAsync(ChatId, new ChatMemberDraft(" ", false), Ct)).Error!.Target.ShouldBe("user@odata.bind");

        _members.AddAsync(ChatId, new ChatMemberDraft("bob-id", true), Ct).Returns(Bob);
        (await new AddChatMember(_members).ExecuteAsync(ChatId, new ChatMemberDraft(" bob-id ", true), Ct)).Value.ShouldBeSameAs(Bob);
    }

    [Fact]
    public void Every_use_case_is_registered()
    {
        var services = new ServiceCollection().AddChatsUseCases();
        var types = typeof(ListChats).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(ListChats).Namespace && t is { IsClass: true, IsSealed: true, IsAbstract: false } && t.GetMethod("ExecuteAsync") is not null);

        foreach (var type in types)
        {
            services.ShouldContain(d => d.ServiceType == type, type.Name);
        }
    }
}

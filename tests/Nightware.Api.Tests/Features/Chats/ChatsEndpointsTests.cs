using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;

using Nightware.Api.Tests.Support;
using Nightware.Application.Chats;
using Nightware.Application.Common.Identity;
using Nightware.Domain.Chats;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Tests.Features.Chats;

/// <summary>Phase 23: the chat routes through the real composition, with the chat ports faked (ADR 0009 D3–D4).</summary>
public sealed class ChatsEndpointsTests : IAsyncLifetime
{
    private const string ChatId = "abcd1234";
    private static readonly DateTimeOffset At = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);
    private static readonly Chat OneOnOne = new(ChatId, ChatType.OneOnOne) { LastUpdatedDateTime = At, WebUrl = "https://cloud.example.com/call/abcd1234" };
    private static readonly Chat Group = new("grp12345", ChatType.Group) { Topic = "Team", Viewpoint = new ChatViewpoint(true, At) };
    private static readonly ChatMember Bob = new("m.18", "Bob Brown") { UserId = "bob-id", Email = "bob@example.com", Roles = [ChatMember.OwnerRole] };

    private static readonly ChatMessage Message = new("59", ChatMessageType.Message, At)
    {
        ChatId = ChatId,
        From = new ChatIdentity("alice-id", "Alice"),
        Body = new ChatMessageBody(ChatBodyType.Html, "<p>Hi <at id=\"0\">Bob Brown</at></p>"),
        Mentions = [new ChatMention(0, "Bob Brown", new ChatMentioned.User(new ChatIdentity("bob-id", "Bob Brown")))],
    };

    private static readonly ChatMessage Renamed = new("77", ChatMessageType.SystemEventMessage, At)
    {
        ChatId = ChatId,
        Body = ChatMessageBody.SystemEvent,
        EventDetail = new ChatEventDetail.ChatRenamed(new ChatIdentity("alice-id", "Alice"), ChatId, "New"),
    };

    private readonly IChatStore _store = Substitute.For<IChatStore>();
    private readonly IChatMembers _members = Substitute.For<IChatMembers>();
    private readonly IChatMessages _messages = Substitute.For<IChatMessages>();
    private readonly IChatPins _pins = Substitute.For<IChatPins>();
    private readonly IMailboxResolver _mailboxes = Substitute.For<IMailboxResolver>();
    private TestHost _host = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient Client => _host.AuthorizedClient();

    public async ValueTask InitializeAsync()
    {
        _mailboxes.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(c => new MailboxRef(c.ArgAt<string?>(0) ?? TestHost.UserEmail));
        _host = await TestHost.StartAsync(services =>
        {
            services.AddScoped(_ => _store);
            services.AddScoped(_ => _members);
            services.AddScoped(_ => _messages);
            services.AddScoped(_ => _pins);
            services.AddScoped(_ => _mailboxes);
        });
    }

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private async Task<JsonObject> GetAsync(string path, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await Client.GetAsync(path, Ct);
        response.StatusCode.ShouldBe(expected, await response.Content.ReadAsStringAsync(Ct));
        return await response.ReadObjectAsync();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, HttpStatusCode expected)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        var response = await Client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(expected, await response.Content.ReadAsStringAsync(Ct));
        return response;
    }

    [Theory]
    [InlineData("/v1.0/me/chats")]
    [InlineData("/v1.0/chats")]
    [InlineData("/beta/users/me@example.com/chats")]
    public async Task Chats_are_listed_in_every_form_with_a_next_link(string path)
    {
        _store.ListAsync(Arg.Is<ChatPageQuery>(q => q.Top == 2 && q.SkipToken == null && !q.WithLastMessage), Arg.Any<CancellationToken>())
            .Returns(new Page<Chat>([OneOnOne, Group], "2"));

        var page = await GetAsync(path + "?$top=2");

        var chats = page["value"]!.AsArray();
        chats[0].Str("chatType").ShouldBe("oneOnOne");
        chats[0]!["topic"].ShouldBeNull();
        chats[0].Str("webUrl").ShouldBe("https://cloud.example.com/call/abcd1234");
        chats[1].Str("chatType").ShouldBe("group");
        chats[1]!["viewpoint"]!["isHidden"]!.GetValue<bool>().ShouldBeTrue();
        page["@odata.nextLink"]!.GetValue<string>().ShouldEndWith("skiptoken=2");
    }

    [Fact]
    public async Task Another_users_chats_are_forbidden()
    {
        await GetAsync("/v1.0/users/bob@example.com/chats", HttpStatusCode.Forbidden);
        await GetAsync($"/v1.0/users/bob@example.com/chats/{ChatId}/messages", HttpStatusCode.Forbidden);
        using var _ = await SendAsync(HttpMethod.Post, $"/v1.0/users/bob@example.com/chats/{ChatId}/messages/59/softDelete", null, HttpStatusCode.Forbidden);
        await _store.DidNotReceiveWithAnyArgs().ListAsync(default!, Ct);
    }

    [Theory]
    [InlineData("$top=51")]
    [InlineData("$top=0")]
    [InlineData("$filter=chatType eq 'group'")]
    [InlineData("$expand=installedApps")]
    [InlineData("$orderby=topic")]
    [InlineData("$search=team")]
    public async Task Unsupported_list_options_are_400(string option) =>
        await GetAsync("/v1.0/me/chats?" + option, HttpStatusCode.BadRequest);

    [Fact]
    public async Task Members_and_the_last_message_expand_on_a_listing()
    {
        _store.ListAsync(Arg.Is<ChatPageQuery>(q => q.WithLastMessage && q.Top == ChatLimits.DefaultPageSize), Arg.Any<CancellationToken>())
            .Returns(new Page<Chat>([OneOnOne with { LastMessage = Message }]));
        _members.ListAsync(ChatId, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<ChatMember>>.Success([Bob]));

        var page = await GetAsync("/v1.0/me/chats?$expand=members,lastMessagePreview&$orderby=lastMessagePreview/createdDateTime desc");

        var chat = page["value"]![0]!;
        chat["members"]![0]!["@odata.type"]!.GetValue<string>().ShouldBe("#microsoft.graph.aadUserConversationMember");
        chat["lastMessagePreview"].Str("id").ShouldBe("59");
        chat["lastMessagePreview"].Str("from.user.id").ShouldBe("alice-id");
        page["@odata.nextLink"].ShouldBeNull();
    }

    [Fact]
    public async Task A_chat_nobody_has_written_in_previews_null_as_graphs_does()
    {
        _store.ListAsync(Arg.Any<ChatPageQuery>(), Arg.Any<CancellationToken>())
            .Returns(new Page<Chat>([OneOnOne with { NeverWritten = true }, Group]));

        var expanded = await GetAsync("/v1.0/me/chats?$expand=lastMessagePreview");
        var plain = await GetAsync("/v1.0/me/chats");

        // Never written in: the key is there, null. Not known (no preview to hand) or not asked for: left out.
        var empty = expanded["value"]![0]!.AsObject();
        empty.ContainsKey("lastMessagePreview").ShouldBeTrue();
        empty["lastMessagePreview"].ShouldBeNull();
        empty.Str("id").ShouldBe(ChatId);
        expanded["value"]![1]!.AsObject().ContainsKey("lastMessagePreview").ShouldBeFalse();
        plain["value"]![0]!.AsObject().ContainsKey("lastMessagePreview").ShouldBeFalse();
    }

    [Fact]
    public async Task A_chat_is_read_with_or_without_members_and_404s_when_missing()
    {
        _store.GetAsync(ChatId, Arg.Any<CancellationToken>()).Returns(OneOnOne);
        _store.GetAsync("gone1234", Arg.Any<CancellationToken>()).Returns(Error.NotFound("gone"));
        _members.ListAsync(ChatId, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<ChatMember>>.Success([Bob]));

        (await GetAsync($"/v1.0/chats/{ChatId}")).Str("id").ShouldBe(ChatId);
        (await GetAsync($"/v1.0/me/chats/{ChatId}?$expand=members"))["members"]![0].Str("userId").ShouldBe("bob-id");
        await GetAsync($"/v1.0/chats/{ChatId}?$expand=lastMessagePreview", HttpStatusCode.BadRequest);
        await GetAsync("/v1.0/chats/gone1234", HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Creating_a_chat_reads_members_from_their_binds()
    {
        _store.CreateAsync(Arg.Any<ChatCreation>(), Arg.Any<CancellationToken>()).Returns(Group);

        using var created = await SendAsync(HttpMethod.Post, "/v1.0/chats", new Dictionary<string, object>
        {
            ["chatType"] = "group",
            ["topic"] = "Team",
            ["members"] = new object[]
            {
                new Dictionary<string, object> { ["@odata.type"] = "#microsoft.graph.aadUserConversationMember", ["roles"] = new[] { "owner" }, ["user@odata.bind"] = "https://graph.microsoft.com/v1.0/users('user-1')" },
                new Dictionary<string, object> { ["roles"] = Array.Empty<string>(), ["user@odata.bind"] = "https://graph.microsoft.com/v1.0/users/bob-id" },
            },
        }, HttpStatusCode.Created);

        (await created.ReadObjectAsync()).Str("topic").ShouldBe("Team");
        await _store.Received(1).CreateAsync(
            Arg.Is<ChatCreation>(c => c.ChatType == ChatType.Group && c.Topic == "Team" && c.Members.Count == 1 && c.Members[0].User == "bob-id" && !c.Members[0].IsOwner),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("""{"chatType":"channel","members":[{"user@odata.bind":"users('b')"}]}""")]
    [InlineData("""{"chatType":"group"}""")]
    [InlineData("""{"chatType":"group","members":[{"roles":[]}]}""")]
    [InlineData("""{"chatType":"group","members":[{"@odata.type":"#microsoft.graph.skypeUserConversationMember","user@odata.bind":"users('b')"}]}""")]
    [InlineData("""{"chatType":"group","members":[{"roles":["admin"],"user@odata.bind":"users('b')"}]}""")]
    public async Task A_malformed_chat_is_400(string body)
    {
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var response = await Client.PostAsync("/v1.0/chats", content, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await _store.DidNotReceiveWithAnyArgs().CreateAsync(default!, Ct);
    }

    [Fact]
    public async Task A_chat_is_renamed()
    {
        _store.RenameAsync("grp12345", "New", Arg.Any<CancellationToken>()).Returns(Group with { Topic = "New" });

        using var renamed = await SendAsync(HttpMethod.Patch, "/v1.0/chats/grp12345", new { topic = "New" }, HttpStatusCode.OK);
        (await renamed.ReadObjectAsync()).Str("topic").ShouldBe("New");
        using var _ = await SendAsync(HttpMethod.Patch, "/v1.0/chats/grp12345", new { }, HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("hideForUser")]
    [InlineData("unhideForUser")]
    [InlineData("markChatReadForUser")]
    [InlineData("markChatUnreadForUser")]
    public async Task For_user_actions_take_the_callers_id_only(string action)
    {
        _store.SetHiddenAsync(ChatId, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _store.MarkReadAsync(ChatId, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _store.MarkUnreadAsync(ChatId, Arg.Any<CancellationToken>()).Returns(Result.Success());

        using var done = await SendAsync(HttpMethod.Post, $"/v1.0/chats/{ChatId}/{action}", new { user = new { id = "user-1", tenantId = "t" } }, HttpStatusCode.NoContent);
        using var denied = await SendAsync(HttpMethod.Post, $"/v1.0/chats/{ChatId}/{action}", new { user = new { id = "bob-id" } }, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Members_are_listed_read_added_and_removed()
    {
        _members.ListAsync(ChatId, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<ChatMember>>.Success([Bob]));
        _members.GetAsync(ChatId, "m.18", Arg.Any<CancellationToken>()).Returns(Bob);
        _members.GetAsync(ChatId, "m.99", Arg.Any<CancellationToken>()).Returns(Error.NotFound("no"));
        _members.AddAsync(ChatId, new ChatMemberDraft("bob-id", true), Arg.Any<CancellationToken>()).Returns(Bob);
        _members.RemoveAsync(ChatId, "m.18", Arg.Any<CancellationToken>()).Returns(Result.Success());

        var listed = await GetAsync($"/v1.0/chats/{ChatId}/members");
        var member = listed["value"]![0]!;
        member.Str("id").ShouldBe("m.18");
        member["roles"]![0]!.GetValue<string>().ShouldBe("owner");
        member.Str("visibleHistoryStartDateTime").ShouldBe("0001-01-01T00:00:00Z");
        member.Str("email").ShouldBe("bob@example.com");
        (await GetAsync($"/v1.0/me/chats/{ChatId}/members/m.18")).Str("displayName").ShouldBe("Bob Brown");
        await GetAsync($"/v1.0/chats/{ChatId}/members/m.99", HttpStatusCode.NotFound);

        using var added = await SendAsync(HttpMethod.Post, $"/v1.0/chats/{ChatId}/members", new Dictionary<string, object>
        {
            ["@odata.type"] = "#microsoft.graph.aadUserConversationMember",
            ["roles"] = new[] { "owner" },
            ["user@odata.bind"] = "https://graph.microsoft.com/v1.0/users('bob-id')",
        }, HttpStatusCode.Created);
        using var bad = await SendAsync(HttpMethod.Post, $"/v1.0/chats/{ChatId}/members", new { roles = Array.Empty<string>() }, HttpStatusCode.BadRequest);
        using var removed = await SendAsync(HttpMethod.Delete, $"/v1.0/chats/{ChatId}/members/m.18", null, HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Messages_page_with_a_next_link_and_system_events_carry_their_type()
    {
        _messages.ListAsync(ChatId, Arg.Is<ChatPageQuery>(q => q.Top == 2 && q.SkipToken == "80"), Arg.Any<CancellationToken>())
            .Returns(new Page<ChatMessage>([Renamed, Message], "59"));

        var page = await GetAsync($"/v1.0/chats/{ChatId}/messages?$top=2&$skiptoken=80");

        var values = page["value"]!.AsArray();
        values[0].Str("messageType").ShouldBe("systemEventMessage");
        values[0]!["eventDetail"]!["@odata.type"]!.GetValue<string>().ShouldBe("#microsoft.graph.chatRenamedEventMessageDetail");
        values[0].Str("eventDetail.chatDisplayName").ShouldBe("New");
        values[0]!["from"].ShouldBeNull();
        values[1]!["from"]!["user"]!["@odata.type"]!.GetValue<string>().ShouldBe("#microsoft.graph.teamworkUserIdentity");
        values[1].Str("from.user.userIdentityType").ShouldBe("aadUser");
        values[1]!["mentions"]![0]!["mentioned"]!["user"].Str("id").ShouldBe("bob-id");
        values[1].Str("body.contentType").ShouldBe("html");
        page["@odata.nextLink"]!.GetValue<string>().ShouldEndWith("skiptoken=59");
        await GetAsync($"/v1.0/chats/{ChatId}/messages?$filter=lastModifiedDateTime gt 2026-01-01T00:00:00Z", HttpStatusCode.BadRequest);
        await GetAsync($"/v1.0/chats/{ChatId}/messages?$top=51", HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_message_is_read_and_404s_when_missing()
    {
        _messages.GetAsync(ChatId, "59", Arg.Any<CancellationToken>()).Returns(Message);
        _messages.GetAsync(ChatId, "60", Arg.Any<CancellationToken>()).Returns(Error.NotFound("no"));

        var message = await GetAsync($"/v1.0/users/me@example.com/chats/{ChatId}/messages/59");
        message.Str("chatId").ShouldBe(ChatId);
        message.Str("etag").ShouldBe(At.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        await GetAsync($"/v1.0/chats/{ChatId}/messages/60", HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Sending_passes_the_html_and_its_mentions_to_the_port()
    {
        _messages.SendAsync(ChatId, Arg.Any<ChatMessageDraft>(), Arg.Any<CancellationToken>()).Returns(Message);

        using var sent = await SendAsync(HttpMethod.Post, $"/v1.0/chats/{ChatId}/messages", new
        {
            body = new { contentType = "html", content = "<p>Hi <at id=\"0\">Bob</at></p>" },
            mentions = new object[]
            {
                new { id = 0, mentionText = "Bob", mentioned = new { user = new { id = "bob-id", userIdentityType = "aadUser" } } },
                new { id = 1, mentionText = "Everyone", mentioned = new { conversation = new { id = ChatId, conversationIdentityType = "chat" } } },
            },
        }, HttpStatusCode.Created);

        (await sent.ReadObjectAsync()).Str("id").ShouldBe("59");
        await _messages.Received(1).SendAsync(ChatId, Arg.Is<ChatMessageDraft>(d =>
            d.Body.ContentType == ChatBodyType.Html && d.Body.Content == "<p>Hi <at id=\"0\">Bob</at></p>"
            && d.Mentions.Count == 2 && d.Mentions[0].User == "bob-id" && !d.Mentions[0].IsConversation
            && d.Mentions[1].IsConversation && d.Mentions[1].User == null), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"body":{"contentType":"markdown","content":"x"}}""")]
    [InlineData("""{"body":{"content":"x"},"mentions":[{"mentionText":"no id"}]}""")]
    [InlineData("""{"body":{"content":""}}""")]
    public async Task A_malformed_message_is_400(string body)
    {
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var response = await Client.PostAsync($"/v1.0/chats/{ChatId}/messages", content, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_message_is_edited_soft_deleted_and_reacted_to()
    {
        _messages.UpdateAsync(ChatId, "59", Arg.Is<ChatMessageDraft>(d => d.Body.ContentType == ChatBodyType.Text && d.Body.Content == "edited"), Arg.Any<CancellationToken>())
            .Returns(Result.Success());
        _messages.SoftDeleteAsync(ChatId, "59", Arg.Any<CancellationToken>()).Returns(Result.Success());
        _messages.SetReactionAsync(ChatId, "59", "👍", Arg.Any<CancellationToken>()).Returns(Result.Success());
        _messages.UnsetReactionAsync(ChatId, "59", "💘", Arg.Any<CancellationToken>()).Returns(Result.Success());

        using var edited = await SendAsync(HttpMethod.Patch, $"/v1.0/chats/{ChatId}/messages/59", new { body = new { contentType = "text", content = "edited" } }, HttpStatusCode.NoContent);
        using var deleted = await SendAsync(HttpMethod.Post, $"/v1.0/me/chats/{ChatId}/messages/59/softDelete", null, HttpStatusCode.NoContent);
        using var liked = await SendAsync(HttpMethod.Post, $"/v1.0/chats/{ChatId}/messages/59/setReaction", new { reactionType = "like" }, HttpStatusCode.NoContent);
        using var unliked = await SendAsync(HttpMethod.Post, $"/v1.0/chats/{ChatId}/messages/59/unsetReaction", new { reactionType = "💘" }, HttpStatusCode.NoContent);
        using var empty = await SendAsync(HttpMethod.Post, $"/v1.0/chats/{ChatId}/messages/59/setReaction", new { }, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Pins_are_listed_with_their_message_when_expanded_added_and_removed()
    {
        _pins.ListAsync(ChatId, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<PinnedChatMessage>>.Success([new PinnedChatMessage("59", Message)]));
        _pins.PinAsync(ChatId, "59", Arg.Any<CancellationToken>()).Returns(new PinnedChatMessage("59", Message));
        _pins.UnpinAsync(ChatId, "59", Arg.Any<CancellationToken>()).Returns(Result.Success());

        (await GetAsync($"/v1.0/chats/{ChatId}/pinnedMessages"))["value"]![0]!["message"].ShouldBeNull();
        (await GetAsync($"/v1.0/chats/{ChatId}/pinnedMessages?$expand=message"))["value"]![0]!["message"].Str("id").ShouldBe("59");
        await GetAsync($"/v1.0/chats/{ChatId}/pinnedMessages?$expand=chat", HttpStatusCode.BadRequest);

        using var pinned = await SendAsync(HttpMethod.Post, $"/v1.0/chats/{ChatId}/pinnedMessages",
            new Dictionary<string, string> { ["message@odata.bind"] = $"https://graph.microsoft.com/v1.0/chats/{ChatId}/messages/59" }, HttpStatusCode.Created);
        (await pinned.ReadObjectAsync()).Str("id").ShouldBe("59");
        using var other = await SendAsync(HttpMethod.Post, $"/v1.0/chats/{ChatId}/pinnedMessages",
            new Dictionary<string, string> { ["message@odata.bind"] = "https://graph.microsoft.com/v1.0/chats/zzzz9999/messages/59" }, HttpStatusCode.BadRequest);
        using var unpinned = await SendAsync(HttpMethod.Delete, $"/v1.0/chats/{ChatId}/pinnedMessages/59", null, HttpStatusCode.NoContent);
        await _pins.DidNotReceive().PinAsync(ChatId, Arg.Is<string>(s => s != "59"), Arg.Any<CancellationToken>());
    }
}

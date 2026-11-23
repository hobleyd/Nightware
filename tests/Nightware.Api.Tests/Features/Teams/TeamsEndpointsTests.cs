using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

using Microsoft.Extensions.DependencyInjection;

using Nightware.Api.Tests.Support;
using Nightware.Application.Chats;
using Nightware.Application.Common.Identity;
using Nightware.Application.Files.Drives;
using Nightware.Application.Teams;
using Nightware.Domain.Chats;
using Nightware.Domain.Files;
using Nightware.Domain.Kernel;
using Nightware.Domain.Teams;

namespace Nightware.Api.Tests.Features.Teams;

/// <summary>Phase 24: the team and channel routes through the real composition, with the team ports faked (ADR 0009 D5).</summary>
public sealed class TeamsEndpointsTests : IAsyncLifetime
{
    private const string TeamId = "6LOrxifQ5p4vRt2I3iplImWqdpjlADT";
    private const string ChannelId = "xtugjbpy";
    private const string Membership = "m.zjioHBheH4iNehqhIX2yQrHrn6RVDCd";
    private const string Base = $"/v1.0/teams/{TeamId}";
    private const string ChannelPath = $"{Base}/channels/{ChannelId}";
    private static readonly DateTimeOffset At = new(2026, 11, 23, 9, 0, 0, TimeSpan.Zero);
    private static readonly Team Team = new(TeamId, "Probe") { Description = "About", Visibility = TeamVisibility.Public, CreatedDateTime = At, WebUrl = "https://cloud.example.com/t" };
    private static readonly Channel General = new(ChannelId, "General") { IsPrimary = true, WebUrl = "https://cloud.example.com/call/xtugjbpy" };
    private static readonly ChatMember Bob = new(Membership, "Bob Brown") { UserId = "bob-id", Email = "bob@example.com" };

    private static readonly ChatMessage Post = new("209", ChatMessageType.Message, At)
    {
        Subject = "Kick-off",
        ChannelIdentity = new ChannelIdentity(TeamId, ChannelId),
        From = new ChatIdentity("alice-id", "Alice"),
        Body = new ChatMessageBody(ChatBodyType.Html, "<p>Agenda</p>"),
    };

    private static readonly ChatMessage Reply = Post with { Id = "212", Subject = null, ReplyToId = "209", Body = new ChatMessageBody(ChatBodyType.Html, "<p>ok</p>") };

    private readonly ITeamStore _teams = Substitute.For<ITeamStore>();
    private readonly ITeamMembers _members = Substitute.For<ITeamMembers>();
    private readonly IChannelStore _channels = Substitute.For<IChannelStore>();
    private readonly IChannelMessages _messages = Substitute.For<IChannelMessages>();
    private readonly IDriveStore _drive = Substitute.For<IDriveStore>();
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
            services.AddScoped(_ => _teams);
            services.AddScoped(_ => _members);
            services.AddScoped(_ => _channels);
            services.AddScoped(_ => _messages);
            services.AddScoped(_ => _drive);
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
    [InlineData("/v1.0/me/joinedTeams")]
    [InlineData("/beta/me/joinedTeams")]
    [InlineData("/v1.0/users/me@example.com/joinedTeams")]
    public async Task Joined_teams_are_the_callers(string path)
    {
        _teams.ListJoinedAsync(Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<Team>>.Success([Team]));

        var team = (await GetAsync(path))["value"]![0]!;

        team["id"]!.GetValue<string>().ShouldBe(TeamId);
        team["displayName"]!.GetValue<string>().ShouldBe("Probe");
        team["visibility"]!.GetValue<string>().ShouldBe("public");
        team["isArchived"]!.GetValue<bool>().ShouldBeFalse();
        team["createdDateTime"]!.GetValue<string>().ShouldBe("2026-11-23T09:00:00Z");
        team.AsObject().ContainsKey("tenantId").ShouldBeFalse();
    }

    [Fact]
    public async Task Another_users_teams_are_forbidden() =>
        await GetAsync("/v1.0/users/bob@example.com/joinedTeams", HttpStatusCode.Forbidden);

    [Fact]
    public async Task A_team_is_read_and_a_missing_one_is_not_found()
    {
        _teams.GetAsync(TeamId, Arg.Any<CancellationToken>()).Returns(Team);
        _teams.GetAsync("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz", Arg.Any<CancellationToken>()).Returns(Error.NotFound("no"));

        (await GetAsync(Base))["description"]!.GetValue<string>().ShouldBe("About");
        (await GetAsync($"{Base}?$select=displayName")).AsObject().Select(p => p.Key).ShouldBe(["@odata.context", "id", "displayName"]);
        await GetAsync("/v1.0/teams/zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz", HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Creating_a_team_answers_202_naming_its_operation_and_the_team()
    {
        _teams.CreateAsync(Arg.Is<TeamCreation>(c => c.DisplayName == "Probe" && c.Visibility == TeamVisibility.Public && c.Members.Single().User == "bob-id" && c.Members[0].IsOwner), Arg.Any<CancellationToken>())
            .Returns(Team);

        using var response = await SendAsync(HttpMethod.Post, "/v1.0/teams", new Dictionary<string, object>
        {
            ["template@odata.bind"] = "https://graph.microsoft.com/v1.0/teamsTemplates('standard')",
            ["displayName"] = "Probe",
            ["visibility"] = "Public",
            ["members"] = new[] { new Dictionary<string, object> { ["roles"] = new[] { "owner" }, ["user@odata.bind"] = "https://graph.microsoft.com/v1.0/users('bob-id')" } },
        }, HttpStatusCode.Accepted);

        response.Headers.Location!.OriginalString.ShouldBe($"/teams('{TeamId}')/operations('create.{TeamId}')");
        response.Content.Headers.ContentLocation!.OriginalString.ShouldBe($"/teams('{TeamId}')");
        (await response.Content.ReadAsStringAsync(Ct)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("""{"displayName":"x"}""")]
    [InlineData("""{"template@odata.bind":"https://graph.microsoft.com/v1.0/teamsTemplates('educationClass')","displayName":"x"}""")]
    [InlineData("""{"template@odata.bind":"https://graph.microsoft.com/v1.0/teamsTemplates('standard')","displayName":"x","visibility":"hiddenMembership"}""")]
    [InlineData("""{"template@odata.bind":"https://graph.microsoft.com/v1.0/teamsTemplates('standard')","displayName":"x","members":[{"roles":[]}]}""")]
    [InlineData("""{"template@odata.bind":"https://graph.microsoft.com/v1.0/teamsTemplates('standard')","displayName":""}""")]
    public async Task A_team_needs_the_standard_template_a_name_and_well_formed_members(string body)
    {
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var response = await Client.PostAsync("/v1.0/teams", content, Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync(Ct));
        await _teams.DidNotReceiveWithAnyArgs().CreateAsync(default!, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData($"{Base}/operations/create.{TeamId}")]
    [InlineData($"/v1.0/teams('{TeamId}')/operations('create.{TeamId}')")]
    public async Task The_creation_operation_is_read_at_both_paths(string path)
    {
        _teams.GetAsync(TeamId, Arg.Any<CancellationToken>()).Returns(Team);

        var operation = await GetAsync(path);

        operation["status"]!.GetValue<string>().ShouldBe("succeeded");
        operation["operationType"]!.GetValue<string>().ShouldBe("createTeam");
        operation["targetResourceId"]!.GetValue<string>().ShouldBe(TeamId);
        operation["targetResourceLocation"]!.GetValue<string>().ShouldBe($"/teams('{TeamId}')");
        await GetAsync($"{Base}/operations/other", HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Patching_a_team_answers_204()
    {
        _teams.UpdateAsync(TeamId, new TeamUpdate("New", null, TeamVisibility.Private), Arg.Any<CancellationToken>()).Returns(Result.Success());

        using var ok = await SendAsync(HttpMethod.Patch, Base, new { displayName = "New", visibility = "private" }, HttpStatusCode.NoContent);
        using var bad = await SendAsync(HttpMethod.Patch, Base, new { visibility = "secret" }, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Deleting_a_teams_group_answers_204_and_any_other_group_501()
    {
        _teams.DeleteAsync(TeamId, Arg.Any<CancellationToken>()).Returns(Result.Success());

        using var deleted = await SendAsync(HttpMethod.Delete, $"/v1.0/groups/{TeamId}", null, HttpStatusCode.NoContent);
        using var other = await SendAsync(HttpMethod.Delete, $"/v1.0/groups/{Guid.NewGuid()}", null, HttpStatusCode.NotImplemented);
    }

    [Fact]
    public async Task Members_are_listed_read_added_changed_and_removed()
    {
        _members.ListAsync(TeamId, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<ChatMember>>.Success([Bob]));
        _members.GetAsync(TeamId, Membership, Arg.Any<CancellationToken>()).Returns(Bob);
        _members.AddAsync(TeamId, new ChatMemberDraft("bob-id", false), Arg.Any<CancellationToken>()).Returns(Bob);
        _members.SetOwnerAsync(TeamId, Membership, true, Arg.Any<CancellationToken>()).Returns(Bob with { Roles = [ChatMember.OwnerRole] });
        _members.RemoveAsync(TeamId, Membership, Arg.Any<CancellationToken>()).Returns(Result.Success());

        var listed = (await GetAsync($"{Base}/members"))["value"]![0]!;
        listed["@odata.type"]!.GetValue<string>().ShouldBe("#microsoft.graph.aadUserConversationMember");
        listed["userId"]!.GetValue<string>().ShouldBe("bob-id");
        (await GetAsync($"{Base}/members/{Membership}"))["email"]!.GetValue<string>().ShouldBe("bob@example.com");

        using (var added = await SendAsync(HttpMethod.Post, $"{Base}/members", new Dictionary<string, object>
               {
                   ["@odata.type"] = "#microsoft.graph.aadUserConversationMember",
                   ["roles"] = Array.Empty<string>(),
                   ["user@odata.bind"] = "https://graph.microsoft.com/v1.0/users('bob-id')",
               }, HttpStatusCode.Created))
        {
            (await added.ReadObjectAsync())["id"]!.GetValue<string>().ShouldBe(Membership);
        }

        using (var changed = await SendAsync(HttpMethod.Patch, $"{Base}/members/{Membership}", new Dictionary<string, object>
               {
                   ["@odata.type"] = "#microsoft.graph.aadUserConversationMember", ["roles"] = new[] { "owner" },
               }, HttpStatusCode.OK))
        {
            (await changed.ReadObjectAsync())["roles"]![0]!.GetValue<string>().ShouldBe("owner");
        }

        using var removed = await SendAsync(HttpMethod.Delete, $"{Base}/members/{Membership}", null, HttpStatusCode.NoContent);
        using var noRoles = await SendAsync(HttpMethod.Patch, $"{Base}/members/{Membership}", new { }, HttpStatusCode.BadRequest);
        using var guest = await SendAsync(HttpMethod.Patch, $"{Base}/members/{Membership}", new { roles = new[] { "guest" } }, HttpStatusCode.BadRequest);
        using var noUser = await SendAsync(HttpMethod.Post, $"{Base}/members", new { roles = Array.Empty<string>() }, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Channels_are_listed_read_and_the_primary_one_found()
    {
        _channels.ListAsync(TeamId, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<Channel>>.Success([General]));
        _channels.GetAsync(TeamId, ChannelId, Arg.Any<CancellationToken>()).Returns(General);
        _channels.GetPrimaryAsync(TeamId, Arg.Any<CancellationToken>()).Returns(General);

        var listed = (await GetAsync($"{Base}/channels"))["value"]![0]!;
        listed["membershipType"]!.GetValue<string>().ShouldBe("standard");
        listed["email"]!.GetValue<string>().ShouldBeEmpty();
        listed["webUrl"]!.GetValue<string>().ShouldBe("https://cloud.example.com/call/xtugjbpy");
        listed["createdDateTime"].ShouldBeNull();
        (await GetAsync(ChannelPath))["displayName"]!.GetValue<string>().ShouldBe("General");
        (await GetAsync($"{Base}/primaryChannel"))["id"]!.GetValue<string>().ShouldBe(ChannelId);
    }

    [Fact]
    public async Task Channels_are_made_changed_and_deleted()
    {
        _channels.CreateAsync(TeamId, new ChannelCreation("Design", "UI"), Arg.Any<CancellationToken>()).Returns(new Channel("hktd6xvp", "Design") { Description = "UI" });
        _channels.UpdateAsync(TeamId, ChannelId, new ChannelUpdate(null, "New"), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _channels.DeleteAsync(TeamId, ChannelId, Arg.Any<CancellationToken>()).Returns(Error.Validation("General"));

        using (var created = await SendAsync(HttpMethod.Post, $"{Base}/channels", new { displayName = "Design", description = "UI", membershipType = "standard" }, HttpStatusCode.Created))
        {
            (await created.ReadObjectAsync())["id"]!.GetValue<string>().ShouldBe("hktd6xvp");
        }

        using var privateChannel = await SendAsync(HttpMethod.Post, $"{Base}/channels", new { displayName = "Secret", membershipType = "private" }, HttpStatusCode.BadRequest);
        using var patched = await SendAsync(HttpMethod.Patch, ChannelPath, new { description = "New" }, HttpStatusCode.NoContent);
        using var deleted = await SendAsync(HttpMethod.Delete, ChannelPath, null, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_channels_members_and_files_folder()
    {
        _channels.GetAsync(TeamId, ChannelId, Arg.Any<CancellationToken>()).Returns(General);
        _members.ListAsync(TeamId, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<ChatMember>>.Success([Bob]));
        _channels.FilesFolderPathAsync(TeamId, ChannelId, Arg.Any<CancellationToken>()).Returns(Result<IReadOnlyList<string>>.Success(["Probe", "General"]));
        _drive.GetItemAsync(Arg.Any<DriveItemRef>(), Arg.Any<CancellationToken>()).Returns(new DriveItem("501", "General") { IsFolder = true, ChildCount = 2, ParentId = "490", ParentPath = "/Probe" });

        (await GetAsync($"{ChannelPath}/members"))["value"]!.AsArray().Count.ShouldBe(1);
        var folder = await GetAsync($"{ChannelPath}/filesFolder");
        folder["id"]!.GetValue<string>().ShouldBe("501");
        folder["folder"]!["childCount"]!.GetValue<int>().ShouldBe(2);
        folder["parentReference"]!["path"]!.GetValue<string>().ShouldBe("/drive/root:/Probe");
    }

    [Fact]
    public async Task Posts_are_listed_with_replies_when_expanded()
    {
        _messages.ListPostsAsync(TeamId, ChannelId, Arg.Is<ChatPageQuery>(q => q.Top == 2), Arg.Any<CancellationToken>()).Returns(new Page<ChatMessage>([Post], "209"));
        _messages.ListRepliesAsync(new ChannelMessageRef(TeamId, ChannelId, "209"), Arg.Any<ChatPageQuery>(), Arg.Any<CancellationToken>()).Returns(new Page<ChatMessage>([Reply]));

        var page = await GetAsync($"{ChannelPath}/messages?$top=2&$expand=replies");

        var post = page["value"]![0]!;
        post["subject"]!.GetValue<string>().ShouldBe("Kick-off");
        post["channelIdentity"]!["teamId"]!.GetValue<string>().ShouldBe(TeamId);
        post["replies"]![0]!["replyToId"]!.GetValue<string>().ShouldBe("209");
        page["@odata.nextLink"]!.GetValue<string>().ShouldContain("skiptoken=209");

        var plain = await GetAsync($"{ChannelPath}/messages?$top=2");
        plain["value"]![0]!.AsObject().ContainsKey("replies").ShouldBeFalse();
        await GetAsync($"{ChannelPath}/messages?$expand=hostedContents", HttpStatusCode.BadRequest);
        await GetAsync($"{ChannelPath}/messages?$top=51", HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_post_is_sent_read_and_its_replies_listed_and_sent()
    {
        _messages.PostAsync(TeamId, ChannelId, Arg.Is<ChatMessageDraft>(d => d.Subject == "Kick-off"), Arg.Any<CancellationToken>()).Returns(Post);
        _messages.GetAsync(new ChannelMessageRef(TeamId, ChannelId, "209"), Arg.Any<CancellationToken>()).Returns(Post);
        _messages.GetAsync(new ChannelMessageRef(TeamId, ChannelId, "209", "212"), Arg.Any<CancellationToken>()).Returns(Reply);
        _messages.ListRepliesAsync(new ChannelMessageRef(TeamId, ChannelId, "209"), Arg.Any<ChatPageQuery>(), Arg.Any<CancellationToken>()).Returns(new Page<ChatMessage>([Reply]));
        _messages.ReplyAsync(new ChannelMessageRef(TeamId, ChannelId, "209"), Arg.Any<ChatMessageDraft>(), Arg.Any<CancellationToken>()).Returns(Reply);

        using (var sent = await SendAsync(HttpMethod.Post, $"{ChannelPath}/messages", new { subject = "Kick-off", body = new { content = "Agenda" } }, HttpStatusCode.Created))
        {
            (await sent.ReadObjectAsync())["id"]!.GetValue<string>().ShouldBe("209");
        }

        (await GetAsync($"{ChannelPath}/messages/209"))["chatId"].ShouldBeNull();
        (await GetAsync($"{ChannelPath}/messages/209/replies/212"))["replyToId"]!.GetValue<string>().ShouldBe("209");
        (await GetAsync($"{ChannelPath}/messages/209/replies"))["value"]!.AsArray().Count.ShouldBe(1);
        using (var replied = await SendAsync(HttpMethod.Post, $"{ChannelPath}/messages/209/replies", new { body = new { content = "ok" } }, HttpStatusCode.Created))
        {
            (await replied.ReadObjectAsync())["id"]!.GetValue<string>().ShouldBe("212");
        }

        using var noBody = await SendAsync(HttpMethod.Post, $"{ChannelPath}/messages", new { subject = "x" }, HttpStatusCode.BadRequest);
        using var noReplyBody = await SendAsync(HttpMethod.Post, $"{ChannelPath}/messages/209/replies", new { }, HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/replies/212")]
    public async Task Posts_and_replies_are_edited_deleted_and_reacted_to(string reply)
    {
        var target = new ChannelMessageRef(TeamId, ChannelId, "209", reply.Length == 0 ? null : "212");
        _messages.UpdateAsync(target, Arg.Any<ChatMessageDraft>(), Arg.Any<CancellationToken>()).Returns(Result.Success());
        _messages.SoftDeleteAsync(target, Arg.Any<CancellationToken>()).Returns(Result.Success());
        _messages.SetReactionAsync(target, "👍", Arg.Any<CancellationToken>()).Returns(Result.Success());
        _messages.UnsetReactionAsync(target, "👍", Arg.Any<CancellationToken>()).Returns(Result.Success());
        var path = $"{ChannelPath}/messages/209{reply}";

        using var patched = await SendAsync(HttpMethod.Patch, path, new { body = new { contentType = "html", content = "<p>x</p>" } }, HttpStatusCode.NoContent);
        using var deleted = await SendAsync(HttpMethod.Post, path + "/softDelete", null, HttpStatusCode.NoContent);
        using var set = await SendAsync(HttpMethod.Post, path + "/setReaction", new { reactionType = "like" }, HttpStatusCode.NoContent);
        using var unset = await SendAsync(HttpMethod.Post, path + "/unsetReaction", new { reactionType = "👍" }, HttpStatusCode.NoContent);
        using var badPatch = await SendAsync(HttpMethod.Patch, path, new { }, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_delta_links_onward_with_its_own_token()
    {
        _messages.DeltaAsync(TeamId, ChannelId, null, 5, Arg.Any<CancellationToken>())
            .Returns(DeltaPage<ChatMessage>.Done([new DeltaEntry<ChatMessage>.Upsert(Post)], new DeltaToken("t1")));
        _messages.DeltaAsync(TeamId, ChannelId, new DeltaToken("t1"), 5, Arg.Any<CancellationToken>())
            .Returns(DeltaPage<ChatMessage>.Done([], new DeltaToken("t2")));

        var first = await GetAsync($"{ChannelPath}/messages/delta?$top=5");
        first["value"]![0]!["id"]!.GetValue<string>().ShouldBe("209");
        var link = new Uri(first["@odata.deltaLink"]!.GetValue<string>());
        (await GetAsync(link.PathAndQuery))["value"]!.AsArray().ShouldBeEmpty();

        await GetAsync($"{ChannelPath}/messages/delta?$deltatoken=garbage", HttpStatusCode.Gone);
        await GetAsync($"{ChannelPath}/messages/delta?$filter=subject eq 'x'", HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_ports_are_composed_from_the_dav_adapter()
    {
        await using var host = await TestHost.StartAsync();
        using var scope = host.App.Services.CreateScope();

        foreach (var port in new[] { typeof(ITeamStore), typeof(ITeamMembers), typeof(IChannelStore), typeof(IChannelMessages) })
        {
            scope.ServiceProvider.GetRequiredService(port).GetType().Namespace.ShouldBe("Nightware.Adapters.Dav.Teams");
        }
    }
}

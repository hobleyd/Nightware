using System.Text.Json;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Nightware.Adapters.Dav.Calendar.Talk;
using Nightware.Adapters.Dav.Chats.Talk;
using Nightware.Application.Chats;
using Nightware.Application.Common.Identity;
using Nightware.Application.Directory;
using Nightware.Domain.Chats;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Tests.Chats;

/// <summary>Talk's ids, wire parsing, identities, links, channel rooms and the registration.</summary>
public sealed class TalkPiecesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("5opkrcg6", true)]
    [InlineData("abcd", true)]
    [InlineData("abc", false)]
    [InlineData("ABCD1234", false)]
    [InlineData("abcd/../x", false)]
    [InlineData(null, false)]
    public void Tokens_are_talks_route_requirement(string? token, bool valid) => TalkIds.IsToken(token).ShouldBe(valid);

    [Theory]
    [InlineData("59", true, 59)]
    [InlineData("0", false, 0)]
    [InlineData("-1", false, 0)]
    [InlineData("1e3", false, 0)]
    [InlineData(" 5", false, 0)]
    [InlineData("1234567890123456789", false, 0)]
    public void Message_ids_are_positive_decimals(string id, bool valid, long expected)
    {
        TalkIds.TryMessageId(id, out var parsed).ShouldBe(valid);
        parsed.ShouldBe(expected);
    }

    [Fact]
    public void Membership_ids_are_m_dot_the_attendee_id()
    {
        TalkIds.MembershipId(17).ShouldBe("m.17");
        TalkIds.TryAttendeeId("m.17", out var attendee).ShouldBeTrue();
        attendee.ShouldBe(17);
        TalkIds.TryAttendeeId("17", out _).ShouldBeFalse();
        TalkIds.TryAttendeeId("m.", out _).ShouldBeFalse();
        TalkIds.TryAttendeeId(null, out _).ShouldBeFalse();
    }

    [Fact]
    public void Php_empty_maps_and_missing_members_parse_as_empty()
    {
        using var document = JsonDocument.Parse("""{"id":5,"messageParameters":[],"reactions":[],"timestamp":"not a number"}""");

        var message = TalkMessage.Parse(document.RootElement);

        message.Parameters.ShouldBeEmpty();
        message.ReactionCounts.ShouldBeEmpty();
        message.Timestamp.ShouldBe(DateTimeOffset.UnixEpoch);
        message.ThreadId.ShouldBe(5);
        message.MessageType.ShouldBe(TalkMessage.Comment);
        message.Parent.ShouldBeNull();
        TalkMessage.ParseMany(document.RootElement.GetProperty("reactions")).ShouldBeEmpty();
    }

    [Fact]
    public void Message_parameters_name_people()
    {
        TalkActor.FromParameter(new TalkParameter("user", "bob", "Bob")).ShouldBe(new TalkActor("users", "bob", "Bob"));
        TalkActor.FromParameter(new TalkParameter("guest", "guest/system", "System")).ShouldBe(new TalkActor("system", "guest/system", "System"));
        TalkActor.FromParameter(new TalkParameter("guest", "guest/abc", "Visitor")).ShouldBe(new TalkActor("guests", "guest/abc", "Visitor"));
        TalkActor.FromParameter(new TalkParameter("user", null, "x")).ShouldBeNull();
        TalkActor.FromParameter(new TalkParameter("file", "467", "a.txt"))!.Type.ShouldBe("file");
        TalkActor.FromParameter(null).ShouldBeNull();
    }

    [Theory]
    [InlineData("users", ChatIdentityKind.User)]
    [InlineData("federated_users", ChatIdentityKind.User)]
    [InlineData("guests", ChatIdentityKind.Guest)]
    [InlineData("emails", ChatIdentityKind.Guest)]
    [InlineData("bots", ChatIdentityKind.Application)]
    [InlineData("system", ChatIdentityKind.Application)]
    public void An_actors_type_says_what_kind_of_identity_it_is(string type, ChatIdentityKind kind) =>
        TalkIdentityDefaults.KindOf(new TalkActor(type, "x", null)).ShouldBe(kind);

    [Fact]
    public async Task Identities_are_resolved_once_and_unknown_people_keep_talks_name()
    {
        using var talk = new TalkTestServer();

        (await talk.Identities.LoadAsync(["alice", "alice", "carol", ""], Ct)).IsSuccess.ShouldBeTrue();
        (await talk.Identities.LoadAsync(["alice"], Ct)).IsSuccess.ShouldBeTrue();

        await talk.Names.Received(1).UserOfAccountAsync("alice", Arg.Any<CancellationToken>());
        talk.Identities.Identify(new TalkActor("users", "alice", "A")).ShouldBe(new ChatIdentity("id-alice", "Alice Anderson"));
        talk.Identities.Identify(new TalkActor("users", "carol", "Carol C")).ShouldBe(new ChatIdentity(null, "Carol C"));
        talk.Identities.Identify(new TalkActor("guests", "alice", "Not her")).ShouldBe(new ChatIdentity(null, "Not her", ChatIdentityKind.Guest));
        (await talk.Identities.AccountNameOfAsync("id-bob", Ct)).Value.ShouldBe("bob");
        (await talk.Identities.AccountNameOfAsync("id-nobody", Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task Without_a_directory_nobody_is_known_or_can_be_named()
    {
        var identities = new TalkIdentities(TalkIdentities.NoDirectory);

        (await identities.LoadAsync(["alice"], Ct)).IsSuccess.ShouldBeTrue();
        identities.Identify(new TalkActor("users", "alice", "Alice")).ShouldBe(new ChatIdentity(null, "Alice"));
        (await identities.AccountNameOfAsync("id-alice", Ct)).Error!.Kind.ShouldBe(ErrorKind.Unsupported);
    }

    [Theory]
    [InlineData("https://cloud.example.com/", "https://cloud.example.com/call/abcd1234")]
    [InlineData("https://cloud.example.com/cloud", "https://cloud.example.com/cloud/call/abcd1234")]
    public void Links_sit_on_talks_public_url(string root, string link) =>
        new TalkLinks(Options.Create(new TalkOptions { PublicBaseUrl = new Uri(root) })).ConversationUrl("abcd1234").ShouldBe(link);

    [Fact]
    public void Without_talks_public_url_there_is_no_link() =>
        new TalkLinks(Options.Create(new TalkOptions())).ConversationUrl("abcd1234").ShouldBeNull();

    [Fact]
    public async Task Channel_rooms_are_the_talk_resources_of_the_callers_teams()
    {
        using var talk = new TalkTestServer();
        talk.ReplyFixture("GET", TalkTestServer.Ocs + "apps/circles/circles", "circles.json");
        talk.ReplyFixture("GET", TalkTestServer.Ocs + "teams/Hpj1aAWPsDDAZh4UF1EwHuz86oXYdxv/resources", "team-resources.json");

        (await talk.Channels.TokensAsync(Ct)).ShouldBe(["indwxaa7"]);
    }

    [Fact]
    public async Task A_team_whose_resources_fail_has_no_channels()
    {
        using var talk = new TalkTestServer();
        talk.Reply("GET", TalkTestServer.Ocs + "apps/circles/circles", 200, """[{"id":"abc"},{"id":"../x"},{"name":"no id"}]""");
        talk.Reply("GET", TalkTestServer.Ocs + "teams/abc/resources", 500);

        (await talk.Channels.TokensAsync(Ct)).ShouldBeEmpty();
        talk.Requests.Count.ShouldBe(2);
    }

    [Fact]
    public void The_chat_ports_are_registered_by_the_dav_adapter()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Dav:BaseUrl"] = "http://nextcloud/" }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped(_ => Substitute.For<IBackendCredentials>());
        services.AddScoped(_ => Substitute.For<IUserAccountNames>());
        services.AddDavAdapter(configuration);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IChatStore>().ShouldBeOfType<Nightware.Adapters.Dav.Chats.TalkChatStore>();
        scope.ServiceProvider.GetRequiredService<IChatMembers>().ShouldBeOfType<Nightware.Adapters.Dav.Chats.TalkChatMembers>();
        scope.ServiceProvider.GetRequiredService<IChatMessages>().ShouldBeOfType<Nightware.Adapters.Dav.Chats.TalkChatMessages>();
        scope.ServiceProvider.GetRequiredService<IChatPins>().ShouldBeOfType<Nightware.Adapters.Dav.Chats.TalkChatPins>();
        scope.ServiceProvider.GetRequiredService<TalkMessageFeed>().ShouldNotBeNull();
    }
}

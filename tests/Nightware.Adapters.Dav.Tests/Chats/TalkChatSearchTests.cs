using Nightware.Adapters.Dav.Chats;
using Nightware.Adapters.Dav.Chats.Talk;
using Nightware.Domain.Kernel;

namespace Nightware.Adapters.Dav.Tests.Chats;

/// <summary>
/// Chat search over Talk's <c>talk-message</c> unified search provider, against alice's recorded room list. The search
/// entries are constructed in Talk 25's shape (<c>MessageSearch::commentToSearchResultEntry</c>).
/// </summary>
public sealed class TalkChatSearchTests : IDisposable
{
    private const string Search = TalkTestServer.Ocs + TalkChatClient.SearchPath;
    private const string Group = "oygp6e5d";
    private const string OneToOne = "nge9dkks";
    private const string Channel = "indwxaa7";
    private const string NoteToSelf = "jxcmpbgy";
    private const string Team = "Hpj1aAWPsDDAZh4UF1EwHuz86oXYdxv";
    private readonly TalkTestServer _talk = new();

    public void Dispose() => _talk.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private TalkChatSearch Searcher() => new(_talk.Client, _talk.Channels, _talk.Identities, _talk.Links);

    private static string Entry(string token, long id, string actorType, string actorId, string title, string subline) =>
        $$$"""
        {"thumbnailUrl":"","title":"{{{title}}}","subline":"{{{subline}}}","resourceUrl":"https://cloud.internal/call/{{{token}}}#message_{{{id}}}",
         "icon":"icon-talk","rounded":true,
         "attributes":{"conversation":"{{{token}}}","messageId":"{{{id}}}","actorType":"{{{actorType}}}","actorId":"{{{actorId}}}","timestamp":"1795500000"}}
        """;

    private void Entries(params string[] entries) =>
        _talk.Reply("GET", Search, 200, $$"""{"name":"Messages","isPaginated":true,"entries":[{{string.Join(',', entries)}}],"cursor":25}""");

    private void Rooms()
    {
        _talk.ReplyFixture("GET", TalkTestServer.Rooms, "rooms.json");
        _talk.ReplyFixture("GET", TalkTestServer.Ocs + "apps/circles/circles", "circles.json");
        _talk.ReplyFixture("GET", TalkTestServer.Ocs + $"teams/{Team}/resources", "team-resources.json");
    }

    [Fact]
    public async Task Matches_in_the_callers_chats_become_hits_with_sender_link_and_summary()
    {
        Rooms();
        Entries(
            Entry(OneToOne, 71, "users", "bob", "Bob Brown", "…the budget is done"),
            Entry(Group, 70, "guests", "abc123", "Gina (guest) in recording test 1795387932", "budget draft"));

        var result = await Searcher().SearchAsync("budget", 0, 25, Ct);

        result.Value.MoreAvailable.ShouldBeFalse();
        result.Value.Hits.Count.ShouldBe(2);
        var bob = result.Value.Hits[0];
        bob.Id.ShouldBe("71");
        bob.ChatId.ShouldBe(OneToOne);
        bob.CreatedDateTime.ShouldBe(DateTimeOffset.FromUnixTimeSeconds(1795500000));
        bob.Summary.ShouldBe("...the budget is done");
        bob.From.ShouldBe(new Nightware.Application.Chats.ChatSender("Bob Brown", "bob@example.com"));
        bob.WebUrl.ShouldBe($"https://cloud.example.com/call/{OneToOne}#message_71");
        var guest = result.Value.Hits[1];
        guest.From!.Name.ShouldBe("Gina (guest)");
        guest.From.Address.ShouldBeNull();
        var call = _talk.Requests.First(r => r.RequestMessage!.Path == Search);
        TalkTestServer.Query(call, "term").ShouldBe("budget");
        TalkTestServer.Query(call, "cursor").ShouldBe("0");
        TalkTestServer.Query(call, "limit").ShouldBe("25");
    }

    [Fact]
    public async Task Channels_and_the_note_to_self_are_left_out()
    {
        Rooms();
        Entries(
            Entry(Channel, 80, "users", "alice", "Alice Anderson in General", "budget"),
            Entry(NoteToSelf, 81, "users", "alice", "Alice Anderson in Note to self", "budget"),
            Entry("zzzz9999", 82, "users", "alice", "Alice Anderson in Gone", "budget"),
            Entry(Group, 83, "users", "alice", "Alice Anderson in recording test 1795387932", "budget"));

        var result = await Searcher().SearchAsync("budget", 0, 25, Ct);

        result.Value.Hits.Select(h => h.Id).ShouldBe(["83"]);
    }

    [Fact]
    public async Task A_full_page_says_more_may_match_and_reads_no_rooms_when_empty()
    {
        Entries(Entry(Channel, 80, "users", "alice", "Alice Anderson in General", "budget"), "{\"attributes\":{}}");
        Rooms();

        var full = await Searcher().SearchAsync("budget", 10, 2, Ct);

        full.Value.MoreAvailable.ShouldBeTrue();
        full.Value.Hits.ShouldBeEmpty();
        TalkTestServer.Query(_talk.Requests.First(r => r.RequestMessage!.Path == Search), "cursor").ShouldBe("10");

        using var empty = new TalkTestServer();
        empty.Reply("GET", Search, 200, """{"name":"Messages","isPaginated":true,"entries":[],"cursor":25}""");
        var none = await new TalkChatSearch(empty.Client, empty.Channels, empty.Identities, empty.Links).SearchAsync("budget", 0, 25, Ct);
        none.Value.Hits.ShouldBeEmpty();
        none.Value.MoreAvailable.ShouldBeFalse();
        empty.Requests.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Without_a_public_link_a_hit_has_no_web_url_and_talk_errors_pass_through()
    {
        using var talk = new TalkTestServer(links: false);
        talk.ReplyFixture("GET", TalkTestServer.Rooms, "rooms.json");
        talk.Reply("GET", Search, 200, $$"""{"entries":[{{Entry(OneToOne, 71, "users", "bob", "Bob Brown", "budget")}}]}""");
        var searcher = new TalkChatSearch(talk.Client, talk.Channels, talk.Identities, talk.Links);

        (await searcher.SearchAsync("budget", 0, 25, Ct)).Value.Hits[0].WebUrl.ShouldBeNull();

        _talk.Reply("GET", Search, 503);
        (await Searcher().SearchAsync("budget", 0, 25, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Theory]
    [InlineData("Bob Brown", null, "Bob Brown")]
    [InlineData("Gina (guest) in Design", "Design", "Gina (guest)")]
    [InlineData("Gina in Design", "Other", "Gina in Design")]
    [InlineData("Design", "Design", "Design")]
    [InlineData("  ", null, null)]
    public void The_senders_name_is_the_title_less_its_conversation(string title, string? conversation, string? name) =>
        TalkChatSearch.SenderName(title, conversation).ShouldBe(name);
}

using Nightware.Adapters.Dav.Chats;
using Nightware.Adapters.Dav.Chats.Talk;
using Nightware.Application.Common.Query;
using Nightware.Domain.Chats;
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
    private const string Probe = "5opkrcg6";
    private static readonly SearchQuery Budget = Q(T(SearchField.Any, "budget"));
    private readonly TalkTestServer _talk = new();

    public void Dispose() => _talk.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private TalkChatSearch Searcher() => new(_talk.Client, _talk.Channels, _talk.Identities, _talk.Links);

    private static SearchTerm T(SearchField field, string value, SearchOp op = SearchOp.Contains, bool negated = false) =>
        new(field, op, value) { Negated = negated };

    private static SearchQuery Q(params SearchTerm[] terms) => new(terms);

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

        var result = await Searcher().SearchAsync(Budget, 0, 25, Ct);

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
    public async Task Channel_matches_are_hits_with_their_team_and_the_note_to_self_is_left_out()
    {
        Rooms();
        Entries(
            Entry(Channel, 80, "users", "alice", "Alice Anderson in General", "budget"),
            Entry(NoteToSelf, 81, "users", "alice", "Alice Anderson in Note to self", "budget"),
            Entry("zzzz9999", 82, "users", "alice", "Alice Anderson in Gone", "budget"),
            Entry(Group, 83, "users", "alice", "Alice Anderson in recording test 1795387932", "budget"));

        var result = await Searcher().SearchAsync(Budget, 0, 25, Ct);

        result.Value.Hits.Select(h => h.Id).ShouldBe(["80", "83"]);
        var channel = result.Value.Hits[0];
        channel.Channel.ShouldBe(new ChannelIdentity(Team, Channel));
        channel.ChatId.ShouldBeNull();
        channel.WebUrl.ShouldBe($"https://cloud.example.com/call/{Channel}#message_80");
        result.Value.Hits[1].ChatId.ShouldBe(Group);
        result.Value.Hits[1].Channel.ShouldBeNull();
    }

    [Fact]
    public async Task A_sender_the_directory_knows_and_a_sent_range_are_talks_filters()
    {
        Rooms();
        Entries(
            Entry(OneToOne, 71, "users", "bob", "Bob Brown", "hello"),
            Entry(OneToOne, 72, "users", "alice", "Alice Anderson", "hello"));
        var query = Q(
            T(SearchField.From, "bob@example.com"),
            T(SearchField.Sent, "2026-11-20", SearchOp.GreaterOrEqual),
            T(SearchField.Sent, "2026-11-21", SearchOp.LessThan));

        var result = await Searcher().SearchAsync(query, 0, 25, Ct);

        var call = _talk.Requests.First(r => r.RequestMessage!.Path == Search);
        TalkTestServer.Query(call, "person").ShouldBe("bob");
        TalkTestServer.Query(call, "since").ShouldBe(new DateTimeOffset(2026, 11, 20, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        TalkTestServer.Query(call, "until").ShouldBe(new DateTimeOffset(2026, 11, 20, 23, 59, 59, TimeSpan.Zero).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        TalkTestServer.Query(call, "term").ShouldBeNull();
        // Talk's entries are checked again: the fixture's timestamp is outside the range.
        result.Value.Hits.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_sender_by_name_is_matched_on_the_hits_and_nothing_to_filter_by_searches_everything()
    {
        Rooms();
        Entries(
            Entry(OneToOne, 71, "users", "bob", "Bob Brown", "hello"),
            Entry(OneToOne, 72, "users", "alice", "Alice Anderson", "hello"),
            Entry(Group, 73, "guests", "g1", "Bobby (guest) in recording test 1795387932", "hello"));

        var result = await Searcher().SearchAsync(Q(T(SearchField.From, "bob")), 0, 25, Ct);

        result.Value.Hits.Select(h => h.Id).ShouldBe(["71", "73"]);
        var call = _talk.Requests.First(r => r.RequestMessage!.Path == Search);
        TalkTestServer.Query(call, "person").ShouldBeNull();
        TalkTestServer.Query(call, "since").ShouldBe("1");
    }

    [Fact]
    public async Task Read_state_comes_from_the_rooms_last_read_message_and_to_from_the_one_on_one_partner()
    {
        Rooms();
        Entries(
            Entry(OneToOne, 30, "users", "bob", "Bob Brown", "x"),
            Entry(OneToOne, 20, "users", "bob", "Bob Brown", "x"),
            Entry(OneToOne, 31, "users", "alice", "Alice Anderson", "x"),
            Entry(Group, 32, "users", "bob", "Bob Brown in recording test 1795387932", "x"));

        var unread = await Searcher().SearchAsync(Q(T(SearchField.IsRead, "false", SearchOp.Equals)), 0, 25, Ct);
        var toBob = await Searcher().SearchAsync(Q(T(SearchField.To, "bob")), 0, 25, Ct);
        var toAlice = await Searcher().SearchAsync(Q(T(SearchField.To, "Alice Anderson", SearchOp.Equals)), 0, 25, Ct);

        unread.Value.Hits.Select(h => h.Id).ShouldBe(["30", "32"]);
        toBob.Value.Hits.Select(h => h.Id).ShouldBe(["31"]);
        toAlice.Value.Hits.Select(h => h.Id).ShouldBe(["30", "20"]);
        _talk.Requests.ShouldNotContain(r => r.RequestMessage!.Path.EndsWith("/context", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Attachments_mentions_and_more_text_read_each_message_whole()
    {
        Rooms();
        foreach (var id in new[] { 59, 76, 80 })
        {
            _talk.ReplyFixture("GET", $"{TalkTestServer.Chat}/{Probe}/{id}/context", "chat-history.json");
        }

        _talk.Reply("GET", $"{TalkTestServer.Chat}/{Probe}/58/context", 404);
        Entries(
            Entry(Probe, 80, "users", "alice", "Alice Anderson in p23 probe renamed", "marker check"),
            Entry(Probe, 76, "users", "alice", "Alice Anderson in p23 probe renamed", "report.pdf"),
            Entry(Probe, 59, "users", "alice", "Alice Anderson in p23 probe renamed", "Edited text @Bob Brown"),
            Entry(Probe, 58, "users", "alice", "Alice Anderson in p23 probe renamed", "gone"));

        var files = await Searcher().SearchAsync(Q(T(SearchField.HasAttachments, "true", SearchOp.Equals)), 0, 25, Ct);
        var bob = await Searcher().SearchAsync(Q(T(SearchField.Mentions, "id-bob")), 0, 25, Ct);
        var words = await Searcher().SearchAsync(Q(T(SearchField.Any, "check"), T(SearchField.Any, "marker")), 0, 25, Ct);
        var notMarker = await Searcher().SearchAsync(Q(T(SearchField.Any, "marker", negated: true)), 0, 25, Ct);
        var mentioned = await Searcher().SearchAsync(Q(T(SearchField.IsMentioned, "true", SearchOp.Equals)), 0, 25, Ct);

        files.Value.Hits.Select(h => h.Id).ShouldBe(["76"]);
        bob.Value.Hits.Select(h => h.Id).ShouldBe(["59"]);
        words.Value.Hits.Select(h => h.Id).ShouldBe(["80"]);
        notMarker.Value.Hits.Select(h => h.Id).ShouldBe(["76", "59"]);
        mentioned.Value.Hits.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_message_talk_fails_to_read_fails_the_search()
    {
        Rooms();
        _talk.Reply("GET", $"{TalkTestServer.Chat}/{Probe}/80/context", 503);
        Entries(Entry(Probe, 80, "users", "alice", "Alice Anderson in p23 probe renamed", "marker check"));

        (await Searcher().SearchAsync(Q(T(SearchField.HasAttachments, "true", SearchOp.Equals)), 0, 25, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }

    [Fact]
    public void Plain_text_names_placeholders_as_talk_shows_them()
    {
        var mention = TalkTestServer.Recorded(59);
        var file = TalkTestServer.Recorded(76);

        TalkChatSearch.PlainText(mention).ShouldBe("Edited **text** @Bob Brown");
        TalkChatSearch.PlainText(file).ShouldNotContain("{file}");
    }

    [Fact]
    public async Task A_full_page_says_more_may_match_and_reads_no_rooms_when_empty()
    {
        Entries(Entry(NoteToSelf, 80, "users", "alice", "Alice Anderson in Note to self", "budget"), "{\"attributes\":{}}");
        Rooms();

        var full = await Searcher().SearchAsync(Budget, 10, 2, Ct);

        full.Value.MoreAvailable.ShouldBeTrue();
        full.Value.Hits.ShouldBeEmpty();
        TalkTestServer.Query(_talk.Requests.First(r => r.RequestMessage!.Path == Search), "cursor").ShouldBe("10");

        using var empty = new TalkTestServer();
        empty.Reply("GET", Search, 200, """{"name":"Messages","isPaginated":true,"entries":[],"cursor":25}""");
        var none = await new TalkChatSearch(empty.Client, empty.Channels, empty.Identities, empty.Links).SearchAsync(Budget, 0, 25, Ct);
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

        (await searcher.SearchAsync(Budget, 0, 25, Ct)).Value.Hits[0].WebUrl.ShouldBeNull();

        _talk.Reply("GET", Search, 503);
        (await Searcher().SearchAsync(Budget, 0, 25, Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
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

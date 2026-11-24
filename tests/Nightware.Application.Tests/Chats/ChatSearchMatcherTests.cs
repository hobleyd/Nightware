using Nightware.Application.Chats;
using Nightware.Application.Common.Query;

namespace Nightware.Application.Tests.Chats;

public sealed class ChatSearchMatcherTests
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    private static readonly ChatSearchCandidate Message = new(Noon)
    {
        Sender = ["bob", "Bob Brown", "bob@example.test"],
        Recipients = ["alice", "Alice Archer"],
        IsRead = false,
        Text = "The Q3 budget is attached @Alice Archer",
        HasAttachment = true,
        IsMentioned = true,
        Mentioned = [["alice", "Alice Archer", "4f0c3a6e-1d2b-4c5d-9e8f-0a1b2c3d4e5f", "4f0c3a6e1d2b4c5d9e8f0a1b2c3d4e5f"]],
    };

    private static SearchTerm T(SearchField field, string value, SearchOp op = SearchOp.Contains, bool negated = false) =>
        new(field, op, value) { Negated = negated };

    private static bool Matches(params SearchTerm[] terms) => ChatSearchMatcher.Matches(new SearchQuery(terms), Message);

    [Fact]
    public void Free_text_is_a_case_insensitive_substring() =>
        (Matches(T(SearchField.Any, "BUDGET"), T(SearchField.Any, "q3")), Matches(T(SearchField.Any, "forecast"))).ShouldBe((true, false));

    [Fact]
    public void A_negated_term_excludes() =>
        (Matches(T(SearchField.Any, "budget", negated: true)), Matches(T(SearchField.Any, "forecast", negated: true))).ShouldBe((false, true));

    [Fact]
    public void From_and_to_match_any_name_and_equals_matches_a_whole_one()
    {
        Matches(T(SearchField.From, "brown")).ShouldBeTrue();
        Matches(T(SearchField.From, "bob@example.test", SearchOp.Equals)).ShouldBeTrue();
        Matches(T(SearchField.From, "bo", SearchOp.Equals)).ShouldBeFalse();
        Matches(T(SearchField.To, "alice")).ShouldBeTrue();
        Matches(T(SearchField.To, "bob")).ShouldBeFalse();
    }

    [Theory]
    [InlineData(SearchOp.Equals, "2026-11-20", true)]
    [InlineData(SearchOp.Equals, "2026-11-21", false)]
    [InlineData(SearchOp.GreaterThan, "2026-11-19", true)]
    [InlineData(SearchOp.GreaterThan, "2026-11-20", false)]
    [InlineData(SearchOp.GreaterOrEqual, "2026-11-20", true)]
    [InlineData(SearchOp.LessThan, "2026-11-20", false)]
    [InlineData(SearchOp.LessThan, "2026-11-21", true)]
    [InlineData(SearchOp.LessOrEqual, "2026-11-20", true)]
    [InlineData(SearchOp.GreaterOrEqual, "2026-11-20T12:00:01Z", false)]
    [InlineData(SearchOp.Equals, "not a date", false)]
    public void Sent_compares_utc_days_and_instants(SearchOp op, string value, bool expected) =>
        Matches(T(SearchField.Sent, value, op)).ShouldBe(expected);

    [Fact]
    public void Flags_compare_true_and_false()
    {
        Matches(T(SearchField.IsRead, "false", SearchOp.Equals)).ShouldBeTrue();
        Matches(T(SearchField.IsRead, "true", SearchOp.Equals)).ShouldBeFalse();
        Matches(T(SearchField.HasAttachments, "true", SearchOp.Equals)).ShouldBeTrue();
        Matches(T(SearchField.IsMentioned, "true", SearchOp.Equals)).ShouldBeTrue();
    }

    [Fact]
    public void Mentions_take_a_directory_id_without_dashes_or_a_name()
    {
        Matches(T(SearchField.Mentions, "4f0c3a6e1d2b4c5d9e8f0a1b2c3d4e5f")).ShouldBeTrue();
        Matches(T(SearchField.Mentions, "archer")).ShouldBeTrue();
        Matches(T(SearchField.Mentions, "bob")).ShouldBeFalse();
    }

    [Fact]
    public void One_term_of_each_or_group_must_hold()
    {
        var either = new SearchQuery([]) { AnyOf = [[T(SearchField.Any, "forecast"), T(SearchField.From, "bob")]] };
        var neither = new SearchQuery([]) { AnyOf = [[T(SearchField.Any, "forecast"), T(SearchField.From, "carol")]] };

        (ChatSearchMatcher.Matches(either, Message), ChatSearchMatcher.Matches(neither, Message)).ShouldBe((true, false));
    }

    [Fact]
    public void Unread_text_counts_as_the_backends_match_and_unread_facts_as_no_match()
    {
        var bare = new ChatSearchCandidate(Noon);

        ChatSearchMatcher.Matches(new SearchQuery([T(SearchField.Any, "budget")]), bare).ShouldBeTrue();
        ChatSearchMatcher.Matches(new SearchQuery([T(SearchField.HasAttachments, "true", SearchOp.Equals)]), bare).ShouldBeFalse();
        ChatSearchMatcher.Matches(new SearchQuery([T(SearchField.Mentions, "alice")]), bare).ShouldBeFalse();
    }

    [Fact]
    public void The_message_is_needed_only_beyond_the_backends_one_substring()
    {
        var budget = T(SearchField.Any, "budget");

        ChatSearchMatcher.NeedsMessage(new SearchQuery([budget, T(SearchField.From, "bob"), T(SearchField.IsRead, "false")]), "budget").ShouldBeFalse();
        ChatSearchMatcher.NeedsMessage(new SearchQuery([budget, T(SearchField.Any, "q3")]), "budget").ShouldBeTrue();
        ChatSearchMatcher.NeedsMessage(new SearchQuery([budget]), null).ShouldBeTrue();
        ChatSearchMatcher.NeedsMessage(new SearchQuery([budget, T(SearchField.Any, "budget", negated: true)]), "budget").ShouldBeTrue();
        ChatSearchMatcher.NeedsMessage(new SearchQuery([T(SearchField.IsMentioned, "true", SearchOp.Equals)]), null).ShouldBeTrue();
        ChatSearchMatcher.NeedsMessage(new SearchQuery([]) { AnyOf = [[budget, T(SearchField.From, "bob")]] }, null).ShouldBeTrue();
    }
}

using Nightware.Application.Chats;
using Nightware.Application.Common.Query;

namespace Nightware.Application.Tests.Chats;

public sealed class ChatSearchPlanTests
{
    private static SearchTerm T(SearchField field, string value, SearchOp op = SearchOp.Contains, bool negated = false) =>
        new(field, op, value) { Negated = negated };

    [Fact]
    public void The_longest_positive_free_text_and_the_first_positive_sender_are_pushed_down()
    {
        var plan = ChatSearchPlan.Of(new SearchQuery([
            T(SearchField.Any, "q3"), T(SearchField.Any, "budget"), T(SearchField.Any, "forecasting", negated: true),
            T(SearchField.From, "carol", negated: true), T(SearchField.From, "bob@example.test"),
        ]));

        plan.ShouldBe(new ChatSearchPlan("budget", null, null, "bob@example.test"));
    }

    [Fact]
    public void Or_groups_push_nothing_down() =>
        ChatSearchPlan.Of(new SearchQuery([]) { AnyOf = [[T(SearchField.Any, "a"), T(SearchField.From, "b")]] })
            .ShouldBe(new ChatSearchPlan(null, null, null, null));

    [Theory]
    [InlineData(SearchOp.GreaterOrEqual, "2026-11-20", "2026-11-20T00:00:00Z", null)]
    [InlineData(SearchOp.GreaterThan, "2026-11-20", "2026-11-21T00:00:00Z", null)]
    [InlineData(SearchOp.LessOrEqual, "2026-11-20", null, "2026-11-20T23:59:59Z")]
    [InlineData(SearchOp.LessThan, "2026-11-20", null, "2026-11-19T23:59:59Z")]
    [InlineData(SearchOp.Equals, "2026-11-20", "2026-11-20T00:00:00Z", "2026-11-20T23:59:59Z")]
    [InlineData(SearchOp.GreaterThan, "2026-11-20T08:30:00Z", "2026-11-20T08:30:01Z", null)]
    public void A_sent_term_is_an_inclusive_range_to_the_second(SearchOp op, string value, string? since, string? until)
    {
        var plan = ChatSearchPlan.Of(new SearchQuery([T(SearchField.Sent, value, op)]));

        plan.Since.ShouldBe(since is null ? null : DateTimeOffset.Parse(since, System.Globalization.CultureInfo.InvariantCulture));
        plan.Until.ShouldBe(until is null ? null : DateTimeOffset.Parse(until, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Several_sent_terms_narrow_and_a_negated_one_is_left_to_the_matcher()
    {
        var plan = ChatSearchPlan.Of(new SearchQuery([
            T(SearchField.Sent, "2026-11-01", SearchOp.GreaterOrEqual),
            T(SearchField.Sent, "2026-11-10", SearchOp.GreaterOrEqual),
            T(SearchField.Sent, "2026-11-30", SearchOp.LessThan),
            T(SearchField.Sent, "2026-11-20", SearchOp.LessThan, negated: true),
        ]));

        (plan.Since, plan.Until).ShouldBe((new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 29, 23, 59, 59, TimeSpan.Zero)));
    }
}

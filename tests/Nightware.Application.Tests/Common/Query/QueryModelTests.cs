using Nightware.Application.Common.Query;

namespace Nightware.Application.Tests.Common.Query;

public sealed class QueryModelTests
{
    private static FilterNode.Compare Eq(string property, string value) => new(property, CompareOp.Eq, new FilterValue.Text(value));

    [Fact]
    public void Conjuncts_flattens_nested_ands_in_order()
    {
        var node = new FilterNode.And(new FilterNode.And(Eq("a", "1"), Eq("b", "2")), new FilterNode.And(Eq("c", "3"), Eq("d", "4")));

        node.Conjuncts().ShouldBe(new FilterNode[] { Eq("a", "1"), Eq("b", "2"), Eq("c", "3"), Eq("d", "4") });
    }

    [Fact]
    public void Conjuncts_of_a_non_and_is_itself()
    {
        var or = new FilterNode.Or(Eq("a", "1"), Eq("b", "2"));

        or.Conjuncts().ShouldBe(new FilterNode[] { or });
        new FilterNode.And(or, Eq("c", "3")).Conjuncts().ShouldBe(new FilterNode[] { or, Eq("c", "3") });
    }

    [Fact]
    public void Filter_values_compare_by_value()
    {
        new FilterValue.Text("x").ShouldBe(new FilterValue.Text("x"));
        new FilterValue.Number(1.5m).ShouldNotBe(new FilterValue.Number(2m));
        FilterValue.Null.Instance.ShouldBeSameAs(FilterValue.Null.Instance);
        new FilterNode.In("p", [new FilterValue.Bool(true)]).Values.Count.ShouldBe(1);
    }

    [Fact]
    public void Default_query_selects_everything()
    {
        var query = ItemQuery.Default;

        query.Selects("anything").ShouldBeTrue();
        query.OrderBy.ShouldBeEmpty();
        query.Expand.ShouldBeEmpty();
        query.ExpandOf("attachments").ShouldBeNull();
    }

    [Fact]
    public void Select_limits_what_selects_reports()
    {
        var query = ItemQuery.Default with { Select = new HashSet<string> { "id", "subject" } };

        query.Selects("subject").ShouldBeTrue();
        query.Selects("body").ShouldBeFalse();
    }

    [Fact]
    public void ExpandOf_matches_case_insensitively()
    {
        var clause = new ExpandClause("attachments", new HashSet<string> { "id" });
        var query = ItemQuery.Default with { Expand = [new ExpandClause("event"), clause] };

        query.ExpandOf("Attachments").ShouldBe(clause);
        query.ExpandOf("missing").ShouldBeNull();
    }

    [Fact]
    public void Free_text_joins_unnegated_any_terms()
    {
        var query = new SearchQuery(
        [
            new SearchTerm(SearchField.Any, SearchOp.Contains, "quarterly"),
            new SearchTerm(SearchField.From, SearchOp.Contains, "alice"),
            new SearchTerm(SearchField.Any, SearchOp.Contains, "numbers"),
            new SearchTerm(SearchField.Any, SearchOp.Contains, "draft") { Negated = true },
        ]);

        query.FreeText.ShouldBe("quarterly numbers");
        query.AnyOf.ShouldBeEmpty();
    }

    [Fact]
    public void Free_text_is_null_without_plain_words()
    {
        new SearchQuery([new SearchTerm(SearchField.Subject, SearchOp.Contains, "x")]).FreeText.ShouldBeNull();
        new SearchQuery([]).FreeText.ShouldBeNull();
        new SearchQuery([new SearchTerm(SearchField.Any, SearchOp.Contains, "x") { Negated = true }]).FreeText.ShouldBeNull();
    }
}

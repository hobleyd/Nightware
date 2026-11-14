using Nightware.Api.Graph.OData;
using Nightware.Application.Common.Query;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Tests.Graph.OData;

public sealed class KqlParserTests
{
    // The client sends $search="<typed text>" with has:attachment rewritten to hasAttachments:true.

    [Fact]
    public void Strips_exactly_the_outer_quotes_and_reads_free_text()
    {
        var query = Parse("\"quarterly numbers\"");

        query.Terms.ShouldBe([Any("quarterly"), Any("numbers")]);
        query.FreeText.ShouldBe("quarterly numbers");
        query.AnyOf.ShouldBeEmpty();
    }

    [Fact]
    public void Reads_the_documented_field_forms()
    {
        var query = Parse("\"from:alex@example.com to:me subject:budget hasAttachments:true report\"");

        query.Terms.ShouldBe(
        [
            new SearchTerm(SearchField.From, SearchOp.Contains, "alex@example.com"),
            new SearchTerm(SearchField.To, SearchOp.Contains, "me"),
            new SearchTerm(SearchField.Subject, SearchOp.Contains, "budget"),
            new SearchTerm(SearchField.HasAttachments, SearchOp.Equals, "true"),
            Any("report"),
        ]);
    }

    [Theory]
    [InlineData("FROM:alice", SearchField.From)]
    [InlineData("Cc:bob", SearchField.Cc)]
    [InlineData("participants:carol", SearchField.Participants)]
    [InlineData("body:invoice", SearchField.Body)]
    [InlineData("attachment:report.pdf", SearchField.Attachment)]
    public void Field_names_are_case_insensitive(string kql, SearchField field)
    {
        Parse($"\"{kql}\"").Terms.Single().Field.ShouldBe(field);
    }

    [Fact]
    public void Quoted_field_values_and_inner_phrases_survive_unescaped_quotes()
    {
        var query = Parse("\"subject:\"weekly report\" \"exact phrase\" budget\"");

        query.Terms.ShouldBe(
        [
            new SearchTerm(SearchField.Subject, SearchOp.Contains, "weekly report"),
            Any("exact phrase"),
            Any("budget"),
        ]);
    }

    [Theory]
    [InlineData("received>=2026-01-01", SearchField.Received, SearchOp.GreaterOrEqual, "2026-01-01")]
    [InlineData("received<=2026-01-31", SearchField.Received, SearchOp.LessOrEqual, "2026-01-31")]
    [InlineData("sent>2026-01-01T10:00:00+10:00", SearchField.Sent, SearchOp.GreaterThan, "2026-01-01T00:00:00Z")]
    [InlineData("sent<2026-02-01", SearchField.Sent, SearchOp.LessThan, "2026-02-01")]
    [InlineData("received:2026-01-15", SearchField.Received, SearchOp.Equals, "2026-01-15")]
    [InlineData("subject=Hello", SearchField.Subject, SearchOp.Equals, "Hello")]
    [InlineData("hasattachment:yes", SearchField.HasAttachments, SearchOp.Equals, "true")]
    [InlineData("hasAttachments:false", SearchField.HasAttachments, SearchOp.Equals, "false")]
    public void Reads_comparison_and_equality_operators(string kql, SearchField field, SearchOp op, string value)
    {
        Parse($"\"{kql}\"").Terms.Single().ShouldBe(new SearchTerm(field, op, value));
    }

    [Theory]
    [InlineData("re: budget", new[] { "re:", "budget" })]
    [InlineData("meeting at 10:30", new[] { "meeting", "at", "10:30" })]
    [InlineData("https://example.com/x", new[] { "https://example.com/x" })]
    [InlineData("foo:bar", new[] { "foo:bar" })]
    [InlineData("received:yesterday", new[] { "received:yesterday" })]
    [InlineData("hasAttachments:maybe", new[] { "hasAttachments:maybe" })]
    [InlineData("from>alice", new[] { "from>alice" })]
    [InlineData("and or not", new[] { "and", "or", "not" })]
    public void User_text_that_is_not_kql_is_free_text(string kql, string[] words)
    {
        var query = Parse($"\"{kql}\"");

        query.Terms.ShouldBe(words.Select(Any).ToList());
    }

    [Fact]
    public void Or_becomes_an_any_of_group()
    {
        var query = Parse("\"from:alice OR from:bob budget\"");

        query.Terms.ShouldBe([Any("budget")]);
        query.AnyOf.Single().ShouldBe([new SearchTerm(SearchField.From, SearchOp.Contains, "alice"), new SearchTerm(SearchField.From, SearchOp.Contains, "bob")]);
    }

    [Fact]
    public void Explicit_and_is_the_same_as_adjacency()
    {
        Parse("\"alpha AND beta\"").Terms.ShouldBe([Any("alpha"), Any("beta")]);
    }

    [Fact]
    public void Not_negates_a_term()
    {
        Parse("\"budget NOT from:alice\"").Terms.ShouldBe([Any("budget"), new SearchTerm(SearchField.From, SearchOp.Contains, "alice") { Negated = true }]);
    }

    [Fact]
    public void Not_over_a_group_applies_de_morgan()
    {
        var query = Parse("\"NOT (alpha OR beta)\"");

        query.Terms.ShouldBe([Any("alpha") with { Negated = true }, Any("beta") with { Negated = true }]);
        query.FreeText.ShouldBeNull();
    }

    [Fact]
    public void Or_of_ands_is_distributed_into_clauses()
    {
        var query = Parse("\"(a AND b) OR c\"");

        query.AnyOf.Count.ShouldBe(2);
        query.AnyOf[0].ShouldBe([Any("a"), Any("c")]);
        query.AnyOf[1].ShouldBe([Any("b"), Any("c")]);
    }

    [Fact]
    public void Stray_operators_and_parentheses_are_tolerated()
    {
        Parse("\"budget OR\"").Terms.ShouldBe([Any("budget"), Any("OR")]);
        Parse("\"NOT\"").Terms.ShouldBe([Any("NOT")]);
        Parse("\"alpha) beta\"").Terms.ShouldBe([Any("alpha"), Any("beta")]);
        Parse("\"(alpha beta\"").Terms.ShouldBe([Any("alpha"), Any("beta")]);
        Parse("\"subject:\"unterminated phrase\"").Terms.ShouldBe([new SearchTerm(SearchField.Subject, SearchOp.Contains, "unterminated phrase")]);
    }

    [Fact]
    public void Unquoted_values_are_parsed_as_is()
    {
        Parse("from:alice").Terms.ShouldBe([new SearchTerm(SearchField.From, SearchOp.Contains, "alice")]);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("")]
    [InlineData(null)]
    public void Empty_search_is_rejected(string? value)
    {
        var result = KqlParser.Parse(value);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Kind.ShouldBe(ErrorKind.Validation);
        result.Error.Target.ShouldBe("$search");
    }

    [Fact]
    public void An_expression_that_explodes_in_conjunctive_form_is_rejected()
    {
        var clauses = Enumerable.Range(0, 8).Select(i => $"(a{i} AND b{i})");
        var result = KqlParser.Parse($"\"{string.Join(" OR ", clauses)}\"");

        result.IsSuccess.ShouldBeFalse();
        result.Error.Message.ShouldContain("too complex");
    }

    private static SearchTerm Any(string word) => new(SearchField.Any, SearchOp.Contains, word);

    private static SearchQuery Parse(string value)
    {
        var result = KqlParser.Parse(value);
        result.IsSuccess.ShouldBeTrue(() => result.Error!.Message);
        return result.Value;
    }
}

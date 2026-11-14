using Nightware.Api.Graph.OData;
using Nightware.Application.Common.Query;
using Nightware.Domain.Kernel;

namespace Nightware.Api.Tests.Graph.OData;

public sealed class FilterParserTests
{
    private const string ConversationA = "AAQkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OAAQAO8aPpjxtUhAlI2JxnVmPSI=";
    private const string ConversationB = "AAQkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OAAQAH1bQqkyxUhAlI2JxnVmPSI=";
    private const string DeletedItems = "AAMkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OAAuAAAAAAAiQ8W967B7TKBjgx9rVEURAQAiIsqMbYjsT5e-T7KzowPTAAAAAAEKAAA=";
    private const string JunkEmail = "AAMkAGVmMDEzMTM4LTZmYWUtNDdkNC1hMDZiLTU1OGY5OTZhYmY4OAAuAAAAAAAiQ8W967B7TKBjgx9rVEURAQAiIsqMbYjsT5e-T7KzowPTAAAAAAEjAAA=";

    // ---- every form the client sends (docs/contract/odata-subset.md §3) ----

    [Fact]
    public void Parses_isRead_eq_false()
    {
        Parse("isRead eq false").ShouldBe(new FilterNode.Compare("isRead", CompareOp.Eq, new FilterValue.Bool(false)));
    }

    [Fact]
    public void Parses_conversationId_eq()
    {
        Parse($"conversationId eq '{ConversationA}'")
            .ShouldBe(new FilterNode.Compare("conversationId", CompareOp.Eq, new FilterValue.Text(ConversationA)));
    }

    [Fact]
    public void Parses_conversationId_eq_with_folder_exclusions()
    {
        var node = Parse($"conversationId eq '{ConversationA}' and parentFolderId ne '{DeletedItems}' and parentFolderId ne '{JunkEmail}'");

        node.Conjuncts().ToList().ShouldBe(
        [
            new FilterNode.Compare("conversationId", CompareOp.Eq, new FilterValue.Text(ConversationA)),
            new FilterNode.Compare("parentFolderId", CompareOp.Ne, new FilterValue.Text(DeletedItems)),
            new FilterNode.Compare("parentFolderId", CompareOp.Ne, new FilterValue.Text(JunkEmail)),
        ]);
    }

    [Fact]
    public void Parses_conversationId_in_list_without_spaces()
    {
        var node = Parse($"conversationId in ('{ConversationA}','{ConversationB}')").ShouldBeOfType<FilterNode.In>();

        node.Property.ShouldBe("conversationId");
        node.Values.ShouldBe([new FilterValue.Text(ConversationA), new FilterValue.Text(ConversationB)]);
    }

    [Fact]
    public void Parses_conversationId_in_list_with_folder_exclusions()
    {
        var conjuncts = Parse($"conversationId in ('{ConversationA}','{ConversationB}') and parentFolderId ne '{DeletedItems}' and parentFolderId ne '{JunkEmail}'")
            .Conjuncts().ToList();

        conjuncts.Count.ShouldBe(3);
        conjuncts[0].ShouldBeOfType<FilterNode.In>().Values.Count.ShouldBe(2);
        conjuncts[2].ShouldBe(new FilterNode.Compare("parentFolderId", CompareOp.Ne, new FilterValue.Text(JunkEmail)));
    }

    [Fact]
    public void Parses_fifteen_item_in_list()
    {
        var ids = Enumerable.Range(0, 15).Select(i => $"'conv{i}'");

        Parse($"conversationId in ({string.Join(',', ids)})").ShouldBeOfType<FilterNode.In>().Values.Count.ShouldBe(15);
    }

    [Fact]
    public void Parses_iCalUId_eq_with_doubled_quote()
    {
        Parse("iCalUId eq '040000008200E00074C5B7101A82E008''x'")
            .ShouldBe(new FilterNode.Compare("iCalUId", CompareOp.Eq, new FilterValue.Text("040000008200E00074C5B7101A82E008'x")));
    }

    [Fact]
    public void Parses_status_ne_completed()
    {
        Parse("status ne 'completed'").ShouldBe(new FilterNode.Compare("status", CompareOp.Ne, new FilterValue.Text("completed")));
    }

    // ---- the wider grammar FilterNode models ----

    [Theory]
    [InlineData("a gt 1", CompareOp.Gt)]
    [InlineData("a ge 1", CompareOp.Ge)]
    [InlineData("a lt 1", CompareOp.Lt)]
    [InlineData("a le 1", CompareOp.Le)]
    [InlineData("a EQ 1", CompareOp.Eq)]
    public void Parses_every_comparison_operator(string filter, CompareOp op)
    {
        Parse(filter).ShouldBe(new FilterNode.Compare("a", op, new FilterValue.Number(1)));
    }

    [Fact]
    public void Parses_property_paths()
    {
        Parse("flag/flagStatus eq 'flagged'").ShouldBe(new FilterNode.Compare("flag/flagStatus", CompareOp.Eq, new FilterValue.Text("flagged")));
    }

    [Fact]
    public void Parses_enum_literals()
    {
        Parse("flag/flagStatus eq microsoft.graph.followupFlagStatus'flagged'")
            .ShouldBe(new FilterNode.Compare("flag/flagStatus", CompareOp.Eq, new FilterValue.EnumMember("flagged")));
    }

    [Fact]
    public void Parses_datetime_number_null_and_true_literals()
    {
        Parse("receivedDateTime ge 2026-01-01T00:00:00Z")
            .ShouldBe(new FilterNode.Compare("receivedDateTime", CompareOp.Ge, new FilterValue.Timestamp(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))));
        Parse("start/dateTime lt 2026-03-01T10:30:00+10:00")
            .ShouldBe(new FilterNode.Compare("start/dateTime", CompareOp.Lt, new FilterValue.Timestamp(new DateTimeOffset(2026, 3, 1, 0, 30, 0, TimeSpan.Zero))));
        Parse("size gt -1.5").ShouldBe(new FilterNode.Compare("size", CompareOp.Gt, new FilterValue.Number(-1.5m)));
        Parse("subject eq null").ShouldBe(new FilterNode.Compare("subject", CompareOp.Eq, FilterValue.Null.Instance));
        Parse("isDraft ne true").ShouldBe(new FilterNode.Compare("isDraft", CompareOp.Ne, new FilterValue.Bool(true)));
    }

    [Fact]
    public void Or_binds_looser_than_and()
    {
        var node = Parse("a eq 1 or b eq 2 and c eq 3").ShouldBeOfType<FilterNode.Or>();

        node.Right.ShouldBeOfType<FilterNode.And>();
    }

    [Fact]
    public void Parentheses_and_not_group()
    {
        var node = Parse("not (a eq 1 or b eq 2) and c eq 3").ShouldBeOfType<FilterNode.And>();

        node.Left.ShouldBeOfType<FilterNode.Not>().Operand.ShouldBeOfType<FilterNode.Or>();
    }

    [Theory]
    [InlineData("startswith(displayName,'Al')", "startswith")]
    [InlineData("contains(subject, 'budget')", "contains")]
    [InlineData("endswith(mail,'@example.com')", "endswith")]
    public void Parses_string_functions(string filter, string function)
    {
        var call = Parse(filter).ShouldBeOfType<FilterNode.Call>();

        call.Function.ShouldBe(function);
        call.Argument.ShouldBeOfType<FilterValue.Text>();
    }

    [Fact]
    public void Parses_any_lambda_with_relative_paths()
    {
        Parse("attendees/any(a: a/emailAddress/address eq 'x@example.com')")
            .ShouldBe(new FilterNode.Any("attendees", new FilterNode.Compare("emailAddress/address", CompareOp.Eq, new FilterValue.Text("x@example.com"))));
    }

    [Fact]
    public void Lambda_variable_alone_is_the_element()
    {
        Parse("categories/any(c: c eq 'Red')")
            .ShouldBe(new FilterNode.Any("categories", new FilterNode.Compare(FilterParser.LambdaElement, CompareOp.Eq, new FilterValue.Text("Red"))));
    }

    [Fact]
    public void Functions_inside_lambdas_are_relative()
    {
        var any = Parse("toRecipients/any(r: startswith(r/emailAddress/name,'Al'))").ShouldBeOfType<FilterNode.Any>();

        any.Predicate.ShouldBe(new FilterNode.Call("startswith", "emailAddress/name", new FilterValue.Text("Al")));
    }

    // ---- rejections name the construct ----

    [Theory]
    [InlineData("", "empty")]
    [InlineData("isRead", "comparison")]
    [InlineData("a has 'x'", "'has'")]
    [InlineData("length(subject) eq 3", "'length'")]
    [InlineData("a eq 'x", "Unterminated")]
    [InlineData("a eq 1 b", "'b'")]
    [InlineData("(a eq 1", "end")]
    [InlineData("a in ('x' 'y')", "'y'")]
    [InlineData("attendees/all(a: a/x eq 1)", "'all'")]
    [InlineData("attendees/any()", "lambda")]
    [InlineData("a eq #", "'#'")]
    [InlineData("a eq b", "literal 'b'")]
    [InlineData("1 eq a", "'1'")]
    public void Rejects_unsupported_constructs(string filter, string mention)
    {
        var result = FilterParser.Parse(filter);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Kind.ShouldBe(ErrorKind.Validation);
        result.Error.Target.ShouldBe("$filter");
        result.Error.Message.ShouldContain(mention, Case.Insensitive);
    }

    private static FilterNode Parse(string filter)
    {
        var result = FilterParser.Parse(filter);
        result.IsSuccess.ShouldBeTrue(() => result.Error!.Message);
        return result.Value;
    }
}

using Microsoft.AspNetCore.WebUtilities;

using Nightware.Api.Graph.OData;
using Nightware.Application.Common.Query;
using Nightware.Domain.Kernel;

using SortDirection = Nightware.Application.Common.Query.SortDirection;

namespace Nightware.Api.Tests.Graph.OData;

public sealed class ODataQueryParserTests
{
    private const string MessageListSelect = "id,subject,from,toRecipients,ccRecipients,bccRecipients,bodyPreview,isRead,flag,receivedDateTime,sentDateTime,importance,conversationId,hasAttachments,parentFolderId";

    [Fact]
    public void Parses_the_folder_page_exactly_as_dio_encodes_it()
    {
        // Dio: Uri.encodeQueryComponent — %24 for $, %2C for commas, + for spaces.
        var query = Parse($"%24top=25&%24skip=0&%24select={MessageListSelect.Replace(",", "%2C", StringComparison.Ordinal)}&%24orderby=receivedDateTime+desc");

        query.Top.ShouldBe(25);
        query.Skip.ShouldBe(0);
        query.Select!.Count.ShouldBe(15);
        query.Select.ShouldContain("parentFolderId");
        query.OrderBy.ShouldBe([new OrderBy("receivedDateTime", SortDirection.Descending)]);
    }

    [Fact]
    public void Parses_the_conversation_expansion_with_encoded_filter()
    {
        var query = Parse("%24filter=conversationId+in+%28%27a%27%2C%27b%27%29+and+parentFolderId+ne+%27d%27&%24select=id&%24top=200");

        query.Filter!.Conjuncts().Count().ShouldBe(2);
        query.Top.ShouldBe(200);
    }

    [Fact]
    public void Parses_raw_dollar_names_and_search()
    {
        var query = Parse("$top=50&$select=id,subject&$search=%22from:alice+hasAttachments:true%22");

        query.Top.ShouldBe(50);
        query.Search!.Terms.Count.ShouldBe(2);
    }

    [Fact]
    public void Parses_the_single_message_expand()
    {
        var query = Parse("%24expand=attachments%28%24select%3Did%2Cname%2CcontentType%2Csize%2CisInline%29");

        var expand = query.Expand.ShouldHaveSingleItem();
        expand.NavigationProperty.ShouldBe("attachments");
        expand.Select!.Order().ShouldBe(["contentType", "id", "isInline", "name", "size"]);
        query.ExpandOf("Attachments").ShouldBe(expand);
    }

    [Fact]
    public void Parses_tasks_orderby_and_filter()
    {
        var query = Parse("%24top=200&%24orderby=createdDateTime+desc&%24filter=status+ne+%27completed%27");

        query.OrderBy.ShouldBe([new OrderBy("createdDateTime", SortDirection.Descending)]);
        query.Filter.ShouldBe(new FilterNode.Compare("status", CompareOp.Ne, new FilterValue.Text("completed")));
    }

    [Fact]
    public void Multiple_orderby_clauses_default_to_ascending()
    {
        Parse("$orderby=subject,receivedDateTime desc").OrderBy.ShouldBe(
        [
            new OrderBy("subject", SortDirection.Ascending),
            new OrderBy("receivedDateTime", SortDirection.Descending),
        ]);
    }

    [Fact]
    public void Clamps_large_top_silently()
    {
        Parse("$top=999").Top.ShouldBe(999);
        Parse("$top=5000").Top.ShouldBe(ODataQueryParser.MaxTop);
    }

    [Fact]
    public void Keeps_skiptoken_and_ignores_deltatoken_and_plain_parameters()
    {
        var query = Parse("$skiptoken=abc.def&$deltatoken=x&startDateTime=2026-01-01T00:00:00.000Z&format=pdf");

        query.SkipToken.ShouldBe("abc.def");
        query.Filter.ShouldBeNull();
    }

    [Fact]
    public void Empty_query_is_the_default()
    {
        Parse(string.Empty).ShouldBe(ItemQuery.Default);
    }

    [Theory]
    [InlineData("$count=true", "$count")]
    [InlineData("$format=json", "$format")]
    [InlineData("$apply=groupby", "$apply")]
    [InlineData("$top=0", "$top")]
    [InlineData("$top=-1", "$top")]
    [InlineData("$top=ten", "$top")]
    [InlineData("$skip=-5", "$skip")]
    [InlineData("$top=1&$top=2", "$top")]
    [InlineData("$select=", "$select")]
    [InlineData("$select=flag/flagStatus", "$select")]
    [InlineData("$select=*", "$select")]
    [InlineData("$orderby=subject sideways", "$orderby")]
    [InlineData("$orderby=a b c", "$orderby")]
    [InlineData("$expand=attachments($filter=size gt 1)", "$expand")]
    [InlineData("$expand=attachments($select=id", "$expand")]
    [InlineData("$expand=", "$expand")]
    [InlineData("$filter=isRead", "$filter")]
    [InlineData("$search=%22%22", "$search")]
    public void Rejects_unsupported_options_naming_them(string queryString, string option)
    {
        var result = ODataQueryParser.Parse(QueryHelpers.ParseQuery(queryString));

        result.IsSuccess.ShouldBeFalse();
        result.Error.Kind.ShouldBe(ErrorKind.Validation);
        result.Error.Code.ShouldBe("invalidRequest");
        result.Error.Target.ShouldNotBeNull().ShouldStartWith(option);
    }

    private static ItemQuery Parse(string queryString)
    {
        var result = ODataQueryParser.Parse(QueryHelpers.ParseQuery(queryString));
        result.IsSuccess.ShouldBeTrue(result.Error?.Message);
        return result.Value;
    }
}

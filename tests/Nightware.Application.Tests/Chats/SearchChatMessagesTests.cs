using Nightware.Application.Chats;
using Nightware.Application.Common.Query;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Chats;

public sealed class SearchChatMessagesTests
{
    private static readonly ChatSearchHits None = new([], false);

    private static readonly SearchQuery Budget = new([new SearchTerm(SearchField.Any, SearchOp.Contains, "budget")]);

    private readonly IChatSearch _search = Substitute.For<IChatSearch>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_query_is_searched_in_the_window_asked_for()
    {
        _search.SearchAsync(Budget, 5, 10, Ct).Returns(None);

        (await new SearchChatMessages(_search).ExecuteAsync(Budget, 5, 10, Ct)).IsSuccess.ShouldBeTrue();

        await _search.Received(1).SearchAsync(Budget, 5, 10, Ct);
    }

    [Fact]
    public async Task A_window_larger_than_graphs_is_read_as_its_largest()
    {
        _search.SearchAsync(Budget, 0, SearchChatMessages.MaxSize, Ct).Returns(None);

        (await new SearchChatMessages(_search).ExecuteAsync(Budget, 0, 500, Ct)).IsSuccess.ShouldBeTrue();

        await _search.Received(1).SearchAsync(Budget, 0, 25, Ct);
    }

    [Fact]
    public async Task A_query_of_scope_terms_alone_is_searched()
    {
        var query = new SearchQuery([new SearchTerm(SearchField.IsMentioned, SearchOp.Equals, "true")]);
        _search.SearchAsync(query, 0, 25, Ct).Returns(None);

        (await new SearchChatMessages(_search).ExecuteAsync(query, 0, 25, Ct)).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0, 25, "query.queryString")]
    [InlineData(-1, 25, "from")]
    [InlineData(0, 0, "size")]
    public async Task An_empty_query_or_a_bad_window_is_refused(int from, int size, string target)
    {
        var query = target == "query.queryString" ? new SearchQuery([]) : Budget;

        var result = await new SearchChatMessages(_search).ExecuteAsync(query, from, size, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        result.Error.Target.ShouldBe(target);
        await _search.DidNotReceiveWithAnyArgs().SearchAsync(default!, default, default, Ct);
    }

    [Fact]
    public async Task No_query_is_refused()
    {
        (await new SearchChatMessages(_search).ExecuteAsync(null, 0, 25, Ct)).Error!.Target.ShouldBe("query.queryString");
    }

    [Fact]
    public async Task A_field_chat_messages_lack_is_refused_even_in_an_or()
    {
        var query = new SearchQuery([]) { AnyOf = [[new SearchTerm(SearchField.Any, SearchOp.Contains, "x"), new SearchTerm(SearchField.Subject, SearchOp.Contains, "y")]] };

        var result = await new SearchChatMessages(_search).ExecuteAsync(query, 0, 25, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        await _search.DidNotReceiveWithAnyArgs().SearchAsync(default!, default, default, Ct);
    }
}

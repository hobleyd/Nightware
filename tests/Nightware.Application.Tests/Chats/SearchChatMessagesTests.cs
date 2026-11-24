using Nightware.Application.Chats;
using Nightware.Domain.Kernel;

namespace Nightware.Application.Tests.Chats;

public sealed class SearchChatMessagesTests
{
    private static readonly ChatSearchHits None = new([], false);

    private readonly IChatSearch _search = Substitute.For<IChatSearch>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_text_is_searched_trimmed_in_the_window_asked_for()
    {
        _search.SearchAsync("budget", 5, 10, Ct).Returns(None);

        (await new SearchChatMessages(_search).ExecuteAsync("  budget ", 5, 10, Ct)).IsSuccess.ShouldBeTrue();

        await _search.Received(1).SearchAsync("budget", 5, 10, Ct);
    }

    [Fact]
    public async Task A_window_larger_than_graphs_is_read_as_its_largest()
    {
        _search.SearchAsync("budget", 0, SearchChatMessages.MaxSize, Ct).Returns(None);

        (await new SearchChatMessages(_search).ExecuteAsync("budget", 0, 500, Ct)).IsSuccess.ShouldBeTrue();

        await _search.Received(1).SearchAsync("budget", 0, 25, Ct);
    }

    [Theory]
    [InlineData(null, 0, 25, "query.queryString")]
    [InlineData("  ", 0, 25, "query.queryString")]
    [InlineData("x", -1, 25, "from")]
    [InlineData("x", 0, 0, "size")]
    public async Task A_request_without_text_or_with_a_bad_window_is_refused(string? text, int from, int size, string target)
    {
        var result = await new SearchChatMessages(_search).ExecuteAsync(text, from, size, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.Validation);
        result.Error.Target.ShouldBe(target);
        await _search.DidNotReceiveWithAnyArgs().SearchAsync(default!, default, default, Ct);
    }

    [Fact]
    public async Task Overlong_text_is_refused()
    {
        var result = await new SearchChatMessages(_search).ExecuteAsync(new string('x', SearchChatMessages.MaxTextLength + 1), 0, 25, Ct);

        result.Error!.Target.ShouldBe("query.queryString");
    }
}

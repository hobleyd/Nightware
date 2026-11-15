using Nightware.Application.Common.Query;
using Nightware.Application.Rooms;
using Nightware.Domain.Kernel;
using Nightware.Domain.Rooms;

namespace Nightware.Application.Tests.Rooms;

public sealed class RoomUseCaseTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private readonly IRoomDirectory _rooms = Substitute.For<IRoomDirectory>();

    [Fact]
    public async Task List_passes_the_query_through()
    {
        var query = new ItemQuery { Top = 500 };
        _rooms.ListAsync(query, Ct).Returns(new Page<Room>([new Room("r", "Boardroom", "b@example.com")], "1"));
        (await new ListRooms(_rooms).ExecuteAsync(query, Ct)).Value.SkipToken.ShouldBe("1");
    }

    [Fact]
    public async Task Find_walks_every_page()
    {
        _rooms.ListAsync(Arg.Is<ItemQuery>(q => q.SkipToken == null), Ct).Returns(new Page<Room>([new Room("1", "One", "one@example.com")], "1"));
        _rooms.ListAsync(Arg.Is<ItemQuery>(q => q.SkipToken == "1"), Ct).Returns(new Page<Room>([new Room("2", "Two", "two@example.com")]));

        var found = (await new FindRooms(_rooms).ExecuteAsync(Ct)).Value;

        found.ShouldBe([new EmailAddress("one@example.com", "One"), new EmailAddress("two@example.com", "Two")]);
    }

    [Fact]
    public async Task Find_stops_at_the_page_cap_and_passes_errors_through()
    {
        _rooms.ListAsync(Arg.Any<ItemQuery>(), Ct).Returns(new Page<Room>([new Room("1", "One", "one@example.com")], "again"));
        (await new FindRooms(_rooms).ExecuteAsync(Ct)).Value.Count.ShouldBe(FindRooms.MaxPages);

        _rooms.ListAsync(Arg.Any<ItemQuery>(), Ct).Returns(Error.Forbidden("no"));
        (await new FindRooms(_rooms).ExecuteAsync(Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }
}

using Nightware.Application.Common.Query;
using Nightware.Application.Rooms;
using Nightware.Domain.Kernel;
using Nightware.Domain.Rooms;

namespace Nightware.Application.Tests.Rooms;

public sealed class RoomListsTests
{
    private readonly IRoomDirectory _rooms = Substitute.For<IRoomDirectory>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Nightware HQ", "nightware-hq")]
    [InlineData("  Level 3 / East Wing! ", "level-3-east-wing")]
    [InlineData("Café", "caf")]
    [InlineData("---", "")]
    public void A_building_becomes_a_lower_case_slug(string building, string slug) => RoomLists.Slug(building).ShouldBe(slug);

    [Fact]
    public async Task Rooms_are_grouped_by_building_across_pages()
    {
        _rooms.ListAsync(Arg.Is<ItemQuery>(q => q.SkipToken == null), Arg.Any<CancellationToken>())
            .Returns(new Page<Room>([new Room("a", "A", "a@x") { Building = "HQ", BuildingAddress = "1 Main St" }], "p2"));
        _rooms.ListAsync(Arg.Is<ItemQuery>(q => q.SkipToken == "p2"), Arg.Any<CancellationToken>())
            .Returns(new Page<Room>([new Room("b", "B", "b@x") { Building = "hq " }, new Room("c", "C", "c@x"), new Room("d", "D", "d@x") { Building = "!!" }]));

        var lists = (await new ListRoomLists(_rooms).ExecuteAsync(Ct)).Value;

        lists.ShouldHaveSingleItem().ShouldBe(new RoomList("roomlist-hq", "HQ", "hq@roomlists.invalid") { Address = "1 Main St" });
        (await new ListRoomsInList(_rooms).ExecuteAsync("HQ@RoomLists.Invalid", Ct)).Value.Select(r => r.Id).ShouldBe(["a", "b"]);
    }

    [Theory]
    [InlineData("nowhere@roomlists.invalid")]
    [InlineData("hq@example.com")]
    [InlineData("@roomlists.invalid")]
    [InlineData("no-at-sign")]
    public async Task An_address_that_names_no_building_is_404(string address)
    {
        _rooms.ListAsync(default!, Ct).ReturnsForAnyArgs(new Page<Room>([new Room("a", "A", "a@x") { Building = "HQ" }]));

        (await new ListRoomsInList(_rooms).ExecuteAsync(address, Ct)).Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    [Fact]
    public async Task A_directory_failure_is_returned()
    {
        _rooms.ListAsync(default!, Ct).ReturnsForAnyArgs(Result<Page<Room>>.Failure(Error.BackendUnavailable("down")));

        (await new ListRoomLists(_rooms).ExecuteAsync(Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
        (await new ListRoomsInList(_rooms).ExecuteAsync("hq@roomlists.invalid", Ct)).Error!.Kind.ShouldBe(ErrorKind.BackendUnavailable);
    }
}

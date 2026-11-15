using Nightware.Application.Common.Query;
using Nightware.Application.Rooms;
using Nightware.Conformance.Tests.Harness;
using Nightware.Domain.Kernel;
using Nightware.Domain.Rooms;

namespace Nightware.Conformance.Tests.Arrangements;

/// <summary>Phase 4: the room directory (<c>places</c>) and its beta fallback (<c>findRooms</c>).</summary>
public sealed class RoomsArrangements : IConformanceArrangements
{
    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        yield return new ConformanceArrangement("rooms.places.list", static fakes =>
            fakes.For<IRoomDirectory>()
                .ListAsync(Arg.Is<ItemQuery>(q => q.Top == 500), Arg.Any<CancellationToken>())
                .Returns(new Page<Room>(
                [
                    new Room("3162F1E1-C4C0-604B-51D8-91DA78989EB1", "Boardroom", "boardroom@example.com")
                    {
                        Capacity = 14,
                        Building = "SYD",
                        FloorNumber = 3,
                        IsWheelChairAccessible = true,
                    },
                ])));

        yield return new ConformanceArrangement("rooms.find-rooms", static fakes =>
            fakes.For<IRoomDirectory>()
                .ListAsync(Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>())
                .Returns(new Page<Room>(
                [
                    new Room("boardroom", "Boardroom", "boardroom@example.com"),
                    new Room("huddle-2", "Huddle 2", "huddle2@example.com"),
                ])));
    }
}

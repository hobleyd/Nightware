using Nightware.Application.Common.Query;
using Nightware.Application.Rooms;
using Nightware.Conformance.Tests.Harness;
using Nightware.Domain.Kernel;
using Nightware.Domain.Rooms;

namespace Nightware.Conformance.Tests.Arrangements;

/// <summary>Phase 19's smaller items: room lists (the buildings) and translateExchangeIds.</summary>
public sealed class SmallParityArrangements : IConformanceArrangements
{
    private static void Rooms(PortFakes fakes) =>
        fakes.For<IRoomDirectory>().ListAsync(Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>()).Returns(new Page<Room>(
        [
            new Room("r1", "Room 100", "r100@example.com") { Building = "Building 1", BuildingAddress = "4567 Main Street, Buffalo NY 98052" },
            new Room("cf200", "Conf Room 200", "cf200@example.com") { Building = "Building 2", Capacity = 40, FloorNumber = 2 },
            new Room("lone", "Lone", "lone@example.com"),
        ]));

    public IEnumerable<ConformanceArrangement> Arrangements()
    {
        yield return new ConformanceArrangement("rooms.room-lists.list", Rooms);
        yield return new ConformanceArrangement("rooms.room-lists.rooms", Rooms);

        // No port: Nightware's ids translate to themselves.
        yield return new ConformanceArrangement("mail.translate-exchange-ids", static _ => { });
    }
}

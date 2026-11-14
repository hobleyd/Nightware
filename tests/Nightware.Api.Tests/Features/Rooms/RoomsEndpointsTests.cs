using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Nightware.Api.Tests.Support;
using Nightware.Application.Common.Query;
using Nightware.Application.Rooms;
using Nightware.Domain.Kernel;
using Nightware.Domain.Rooms;

namespace Nightware.Api.Tests.Features.Rooms;

public sealed class RoomsEndpointsTests : IAsyncLifetime
{
    private readonly IRoomDirectory _rooms = Substitute.For<IRoomDirectory>();
    private TestHost _host = null!;

    public async ValueTask InitializeAsync() => _host = await TestHost.StartAsync(services => services.AddSingleton(_rooms));

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Room Boardroom() => new("boardroom", "Boardroom", "boardroom@example.com") { Capacity = 12, Building = "HQ", FloorNumber = 1, FloorLabel = "Level 1", IsWheelChairAccessible = true };

    [Fact]
    public async Task Places_page_with_a_next_link_on_the_graph_base()
    {
        _rooms.ListAsync(Arg.Is<ItemQuery>(q => q.Top == 500), Arg.Any<CancellationToken>()).Returns(new Page<Room>([Boardroom()], "1"));

        var response = await _host.AuthorizedClient().GetAsync("/v1.0/places/microsoft.graph.room?$top=500", Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.ReadObjectAsync();
        var room = body["value"]![0]!;
        room.Str("emailAddress").ShouldBe("boardroom@example.com");
        room.Str("building").ShouldBe("HQ");
        room["floorNumber"]!.GetValue<int>().ShouldBe(1);
        room.Str("floorLabel").ShouldBe("Level 1");
        room.Str("bookingType").ShouldBe("standard");
        body["@odata.nextLink"]!.GetValue<string>().ShouldStartWith(TestHost.GraphBase + "/places/microsoft.graph.room?");
    }

    [Fact]
    public async Task Places_errors_let_the_client_fall_back()
    {
        _rooms.ListAsync(Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>()).Returns(Error.Forbidden("no"));
        (await _host.AuthorizedClient().GetAsync("/v1.0/places/microsoft.graph.room", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Find_rooms_lists_names_and_addresses_on_beta_only()
    {
        _rooms.ListAsync(Arg.Any<ItemQuery>(), Arg.Any<CancellationToken>()).Returns(new Page<Room>([Boardroom(), new Room("f", "Focus", "focus@example.com")]));

        var body = await (await _host.AuthorizedClient().GetAsync("/beta/me/findRooms", Ct)).ReadObjectAsync();
        body["value"]!.AsArray().Select(v => v.Str("address")).ShouldBe(["boardroom@example.com", "focus@example.com"]);
        body["value"]![0].Str("name").ShouldBe("Boardroom");

        var v1 = await _host.AuthorizedClient().GetAsync("/v1.0/me/findRooms", Ct);
        v1.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await v1.ReadObjectAsync()).Str("error.message").ShouldBe("Resource not found for the segment 'findRooms'.");
    }

    [Fact]
    public async Task Find_rooms_has_no_users_form() =>
        (await _host.AuthorizedClient().GetAsync("/beta/users/x@example.com/findRooms", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
}

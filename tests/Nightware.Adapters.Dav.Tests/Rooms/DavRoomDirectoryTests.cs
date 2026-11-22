using Nightware.Adapters.Dav.Rooms;
using Nightware.Application.Common.Query;
using Nightware.Domain.Kernel;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Nightware.Adapters.Dav.Tests.Rooms;

/// <summary>Rooms from calendar_resource_management principals, on a listing recorded from the dev stack.</summary>
public sealed class DavRoomDirectoryTests : IDisposable
{
    private const string Collection = "/remote.php/dav/principals/calendar-rooms/";
    private readonly DavTestServer _dav = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DavRoomDirectory Directory() => new(_dav.Client, _dav.Paths);

    private void Serve(int status, string? body = null) =>
        _dav.Server.Given(Request.Create().WithPath(Collection).UsingMethod("PROPFIND"))
            .RespondWith(Response.Create().WithStatusCode(status).WithBody(body ?? string.Empty));

    [Fact]
    public async Task Lists_room_principals_with_their_metadata()
    {
        Serve(207, Fixture.Read(Path.Combine("Calendar", "rooms-propfind.xml")));

        var rooms = (await Directory().ListAsync(ItemQuery.Default, Ct)).Value.Items;

        rooms.Select(r => r.DisplayName).ShouldBe(["Boardroom", "Focus Room 1"]);
        var boardroom = rooms[0];
        boardroom.Id.ShouldBe("calendar_resource_management-nightware-boardroom");
        boardroom.EmailAddress.ShouldBe("boardroom@nightware.localhost");
        boardroom.Capacity.ShouldBe(12);
        boardroom.Building.ShouldBe("Nightware HQ");
        boardroom.BuildingAddress.ShouldBe("1 Example Street, Sydney NSW 2000");
        boardroom.FloorLabel.ShouldBe("Level 1");
        boardroom.FloorNumber.ShouldBe(1);
        boardroom.IsWheelChairAccessible.ShouldBeTrue();
        rooms[1].IsWheelChairAccessible.ShouldBeFalse();
        var request = _dav.Server.LogEntries.ShouldHaveSingleItem().RequestMessage!;
        request.Headers!["Depth"][0].ShouldBe("1");
        request.Body!.ShouldContain("room-building-name");
    }

    [Fact]
    public async Task Pages_with_top()
    {
        Serve(207, Fixture.Read(Path.Combine("Calendar", "rooms-propfind.xml")));

        var page = (await Directory().ListAsync(new ItemQuery { Top = 1 }, Ct)).Value;

        page.Items.ShouldHaveSingleItem().DisplayName.ShouldBe("Boardroom");
        page.SkipToken.ShouldBe("1");
    }

    [Fact]
    public async Task Rejects_filters_and_passes_errors_through()
    {
        var filtered = new ItemQuery { Filter = new FilterNode.Compare("displayName", CompareOp.Eq, new FilterValue.Text("x")) };
        (await Directory().ListAsync(filtered, Ct)).Error!.Kind.ShouldBe(ErrorKind.Validation);

        Serve(403);
        (await Directory().ListAsync(ItemQuery.Default, Ct)).Error!.Kind.ShouldBe(ErrorKind.Forbidden);
    }

    [Fact]
    public void A_principal_without_an_address_is_not_a_room() =>
        DavRoomDirectory.ToRoom(new Nightware.Adapters.Dav.Core.DavResponse("/x/", null, [])).ShouldBeNull();

    public void Dispose() => _dav.Dispose();
}

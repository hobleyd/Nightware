using System.Globalization;
using System.Xml.Linq;
using Nightware.Adapters.Dav.Calendar;
using Nightware.Adapters.Dav.Core;
using Nightware.Application.Common.Query;
using Nightware.Application.Rooms;
using Nightware.Domain.Kernel;
using Nightware.Domain.Rooms;

namespace Nightware.Adapters.Dav.Rooms;

/// <summary>
/// <see cref="IRoomDirectory"/> over the room principals that Nextcloud's calendar_resource_management app
/// publishes (<c>principals/calendar-rooms/</c>, CUTYPE ROOM, metadata in <c>http://nextcloud.com/ns</c>).
/// Booking needs nothing more: a room attendee (CUTYPE=ROOM) is auto-scheduled by Sabre.
/// </summary>
internal sealed partial class DavRoomDirectory(IDavClient dav, DavPaths paths) : IRoomDirectory
{
    /// <summary>Graph's default page size for places.</summary>
    internal const int DefaultPageSize = 100;

    internal const string RoomsCollection = "principals/calendar-rooms/";
    internal const string WheelchairAccessible = "WHEELCHAIR-ACCESSIBLE";
    // Published by calendar_resource_management (verified against Nextcloud 35) but not in DavNames.
    internal static readonly XName RoomBuildingName = DavNs.Nextcloud + "room-building-name";
    internal static readonly XName RoomBuildingStory = DavNs.Nextcloud + "room-building-story";

    private static readonly XName[] RoomProperties =
    [
        DavNames.DisplayName,
        DavNames.CalendarUserType,
        DavNames.CalendarUserAddressSet,
        DavNames.RoomType,
        DavNames.RoomSeatingCapacity,
        RoomBuildingName,
        DavNames.RoomBuildingAddress,
        RoomBuildingStory,
        DavNames.RoomBuildingRoomNumber,
        DavNames.RoomFeatures,
    ];

    public async Task<Result<Page<Room>>> ListAsync(ItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Filter is not null || query.Search is not null || query.OrderBy.Count > 0)
        {
            return Error.Validation("The room list supports only $top and $skip.", query.Filter is not null ? "$filter" : query.Search is not null ? "$search" : "$orderby");
        }

        var collection = paths.RootPath + RoomsCollection;
        var listing = await dav.PropFindAsync(collection, DavDepth.One, RoomProperties, cancellationToken).ConfigureAwait(false);
        if (!listing.IsSuccess)
        {
            return listing.Error;
        }

        var rooms = listing.Value.Responses
            .Where(r => r.IsFound && !string.Equals(r.Href.TrimEnd('/'), collection.TrimEnd('/'), StringComparison.Ordinal))
            .Where(r => string.Equals(r.Text(DavNames.CalendarUserType)?.Trim(), "ROOM", StringComparison.OrdinalIgnoreCase))
            .Select(ToRoom)
            .OfType<Room>()
            .OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.EmailAddress, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return OffsetPaging.Slice(rooms, query, DefaultPageSize);
    }

    /// <summary>
    /// A room principal as a Graph room: <c>building</c> is the building's name (its address when unnamed),
    /// the floor is the story, numbered from its label (<c>Level 1</c> → 1).
    /// </summary>
    internal static Room? ToRoom(DavResponse response)
    {
        var address = response.HrefsOf(DavNames.CalendarUserAddressSet)
            .Where(a => a.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            .Select(a => Uri.UnescapeDataString(a["mailto:".Length..]).Trim())
            .FirstOrDefault(a => a.Length > 0);
        if (address is null)
        {
            return null;
        }

        var features = (response.Text(DavNames.RoomFeatures) ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var story = Blank(response.Text(RoomBuildingStory));
        return new Room(Uri.UnescapeDataString(response.Href.TrimEnd('/').Split('/')[^1]), Blank(response.Text(DavNames.DisplayName)) ?? address, address)
        {
            Capacity = int.TryParse(response.Text(DavNames.RoomSeatingCapacity), NumberStyles.Integer, CultureInfo.InvariantCulture, out var capacity) ? capacity : null,
            Building = Blank(response.Text(RoomBuildingName)) ?? Blank(response.Text(DavNames.RoomBuildingAddress)),
            BuildingAddress = Blank(response.Text(DavNames.RoomBuildingAddress)),
            FloorLabel = story,
            FloorNumber = story is not null && FloorNumberPattern().Match(story) is { Success: true } match
                ? int.Parse(match.Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)
                : null,
            IsWheelChairAccessible = features.Contains(WheelchairAccessible, StringComparer.OrdinalIgnoreCase),
        };
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [System.Text.RegularExpressions.GeneratedRegex(@"-?\d+")]
    private static partial System.Text.RegularExpressions.Regex FloorNumberPattern();
}

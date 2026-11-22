using System.Text.Json.Serialization;
using Nightware.Domain.Kernel;
using Nightware.Domain.Rooms;

namespace Nightware.Api.Features.Rooms.Dto;

/// <summary>Graph <c>microsoft.graph.room</c>; NightMail reads the address, name, capacity, building, floor and accessibility.</summary>
public sealed record RoomDto
{
    /// <summary>Every room the backend offers is booked through the calendar (auto-scheduled), Graph's <c>standard</c>.</summary>
    public const string StandardBooking = "standard";

    public string? Id { get; init; }

    public string? EmailAddress { get; init; }

    public string? DisplayName { get; init; }

    public int? Capacity { get; init; }

    public string? Building { get; init; }

    public int? FloorNumber { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FloorLabel { get; init; }

    public bool IsWheelChairAccessible { get; init; }

    public string BookingType { get; init; } = StandardBooking;
}

/// <summary>Graph <c>microsoft.graph.roomList</c> (phase 19): a building, as Nightware's room directory knows it.</summary>
public sealed record RoomListDto
{
    public string? Id { get; init; }

    public string? DisplayName { get; init; }

    /// <summary>The directory keeps a building's address as one line, so it is all <c>street</c>.</summary>
    public PhysicalAddressDto? Address { get; init; }

    public object? GeoCoordinates { get; init; }

    public string? Phone { get; init; }

    public string? EmailAddress { get; init; }

    public string? PlaceId { get; init; }
}

/// <summary>Graph <c>physicalAddress</c>.</summary>
public sealed record PhysicalAddressDto
{
    public string? Street { get; init; }

    public string? City { get; init; }

    public string? State { get; init; }

    public string? PostalCode { get; init; }

    public string? CountryOrRegion { get; init; }
}

/// <summary>An entry of beta <c>findRooms</c> (Graph <c>emailAddress</c>).</summary>
public sealed record RoomAddressDto
{
    public string? Name { get; init; }

    public string? Address { get; init; }
}

/// <summary>Maps Domain rooms to the Graph DTOs.</summary>
public static class RoomDtoMapper
{
    public static RoomDto ToDto(Room room)
    {
        ArgumentNullException.ThrowIfNull(room);
        return new RoomDto
        {
            Id = room.Id,
            EmailAddress = room.EmailAddress,
            DisplayName = room.DisplayName,
            Capacity = room.Capacity,
            Building = room.Building,
            FloorNumber = room.FloorNumber,
            FloorLabel = room.FloorLabel,
            IsWheelChairAccessible = room.IsWheelChairAccessible,
        };
    }

    public static RoomListDto ToDto(RoomList list)
    {
        ArgumentNullException.ThrowIfNull(list);
        return new RoomListDto
        {
            Id = list.Id,
            DisplayName = list.DisplayName,
            Address = list.Address is { } street ? new PhysicalAddressDto { Street = street } : null,
            EmailAddress = list.EmailAddress,
            PlaceId = list.Id,
        };
    }

    public static RoomAddressDto ToDto(EmailAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return new RoomAddressDto { Name = address.Name ?? address.Address, Address = address.Address };
    }
}

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

    public static RoomAddressDto ToDto(EmailAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return new RoomAddressDto { Name = address.Name ?? address.Address, Address = address.Address };
    }
}

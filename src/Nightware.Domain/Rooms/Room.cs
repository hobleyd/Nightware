namespace Nightware.Domain.Rooms;

/// <summary>A bookable room (Graph <c>microsoft.graph.room</c>).</summary>
public sealed record Room(string Id, string DisplayName, string EmailAddress)
{
    public int? Capacity { get; init; }

    public string? Building { get; init; }

    public string? FloorLabel { get; init; }

    /// <summary>The floor as a number (Graph <c>floorNumber</c>), when the label names one.</summary>
    public int? FloorNumber { get; init; }

    public bool IsWheelChairAccessible { get; init; }
}

namespace Nightware.Domain.Rooms;

/// <summary>A bookable room (Graph <c>microsoft.graph.room</c>).</summary>
public sealed record Room(string Id, string DisplayName, string EmailAddress)
{
    public int? Capacity { get; init; }

    public string? Building { get; init; }

    public string? FloorLabel { get; init; }

    public bool IsWheelChairAccessible { get; init; }
}
